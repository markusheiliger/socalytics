using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Analysis.Results;

public static class ResultRejectionReasons
{
    public const string ManifestMissing = "result-manifest-missing";
    public const string ManifestTooLarge = "result-manifest-too-large";
    public const string ManifestChecksumMismatch = "result-manifest-checksum-mismatch";
    public const string ManifestSchemaInvalid = "result-manifest-schema-invalid";
    public const string ManifestVersionUnsupported = "result-manifest-version-unsupported";
    public const string ScopeMismatch = "result-scope-mismatch";
    public const string ProvenanceMismatch = "result-provenance-mismatch";
    public const string LineageMismatch = "result-lineage-mismatch";
    public const string ArtifactInvalid = "result-artifact-invalid";
    public const string ValidityInvalid = "result-validity-invalid";
}

public enum ResultVerificationStatus
{
    Valid,
    Rejected,
    StorageUnavailable,
}

public sealed record ValidatedResultManifest(
    string ContractVersion,
    string OutputType,
    long ValidityStartMs,
    long ValidityEndMs,
    int FactCount,
    string ArtifactsJson,
    string UpstreamLineageJson);

public sealed record ResultVerificationOutcome(ResultVerificationStatus Status, string? Reason, ValidatedResultManifest? Manifest)
{
    public static ResultVerificationOutcome Accepted(ValidatedResultManifest manifest) => new(ResultVerificationStatus.Valid, null, manifest);

    public static ResultVerificationOutcome Rejected(string reason) => new(ResultVerificationStatus.Rejected, reason, null);

    public static ResultVerificationOutcome Unavailable() => new(ResultVerificationStatus.StorageUnavailable, null, null);
}

/// <summary>The recorded facts of one succeeded completion the manifest is verified against.</summary>
public sealed record ResultVerificationRequest(
    AnalysisRunRecord Run,
    string RunSnapshotJson,
    WorkflowNodeRecord Node,
    LogicalJobRecord Job,
    LogicalJobSnapshotRecord Snapshot,
    Guid AttemptId,
    int AttemptNumber,
    string ManifestReference,
    string ManifestChecksum,
    IReadOnlyList<AcceptedResultRecord> Upstream);

/// <summary>Performs the ordered manifest checks of the result-acceptance contract; reads storage but never the database transaction.</summary>
public sealed class ResultManifestVerifier(
    IObjectStorage storage,
    IContractValidator validator,
    IResultDeclarationStore declarations,
    IOptions<AnalysisResultOptions> options)
{
    public const string SupportedContractVersion = "1.0.0";

    public async Task<ResultVerificationOutcome> VerifyAsync(ResultVerificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await VerifyCoreAsync(request, cancellationToken);
        }
        catch (ObjectStorageUnavailableException)
        {
            return ResultVerificationOutcome.Unavailable();
        }
    }

    private async Task<ResultVerificationOutcome> VerifyCoreAsync(ResultVerificationRequest request, CancellationToken ct)
    {
        var run = request.Run;
        var job = request.Job;
        var location = IssueResultGrantsHandler.ResultLocation(run.Id, job.Id, request.AttemptId);
        var manifestKey = location + IssueResultGrantsHandler.ManifestName;

        // 1: location and declaration
        var declared = (await declarations.ListByAttemptAsync(request.AttemptId, ct)).ToDictionary(d => d.Name, StringComparer.Ordinal);
        if (!string.Equals(request.ManifestReference, manifestKey, StringComparison.Ordinal)
            || !declared.TryGetValue(IssueResultGrantsHandler.ManifestName, out var manifestDeclaration))
        {
            return Reject(ResultRejectionReasons.ManifestMissing);
        }

        // 2 and 3: existence and size
        var read = await storage.ReadObjectAsync(manifestKey, options.Value.MaxManifestBytes, ct);
        if (read is null)
        {
            return Reject(ResultRejectionReasons.ManifestMissing);
        }

        if (read.TooLarge)
        {
            return Reject(ResultRejectionReasons.ManifestTooLarge);
        }

        // 4: storage-attested checksum equals the reported and the declared digest (fails closed when storage cannot attest)
        var attested = await storage.GetObjectChecksumAsync(manifestKey, ct);
        if (attested is null
            || !Sha256Digest.TryParse(request.ManifestChecksum, out var reported)
            || !Sha256Digest.TryParse(manifestDeclaration.Digest, out var declaredDigest)
            || !attested.Digest.Equals(reported)
            || !attested.Digest.Equals(declaredDigest)
            || !Sha256Digest.FromBytes(SHA256.HashData(read.Content!)).Equals(attested.Digest))
        {
            return Reject(ResultRejectionReasons.ManifestChecksumMismatch);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(read.Content!);
        }
        catch (JsonException)
        {
            return Reject(ResultRejectionReasons.ManifestSchemaInvalid);
        }

        using (document)
        {
            var root = document.RootElement;

            // 5: supported contract version
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("contractVersion", out var declaredVersion)
                && declaredVersion.ValueKind == JsonValueKind.String
                && !string.Equals(declaredVersion.GetString(), SupportedContractVersion, StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(declaredVersion.GetString()!, @"^1\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
            {
                return Reject(ResultRejectionReasons.ManifestVersionUnsupported);
            }

            // 6: schema
            if (!validator.Validate(ContractSchemaIds.ResultManifestV1, root).IsValid)
            {
                return Reject(ResultRejectionReasons.ManifestSchemaInvalid);
            }

            // 7: scope and attempt
            if (!ScopeMatches(request, root, out var window))
            {
                return Reject(ResultRejectionReasons.ScopeMismatch);
            }

            // 8: provenance
            if (!ProvenanceMatches(request, root.GetProperty("provenance")))
            {
                return Reject(ResultRejectionReasons.ProvenanceMismatch);
            }

            // 9: lineage
            if (!LineageMatches(request, root.GetProperty("upstreamLineage")))
            {
                return Reject(ResultRejectionReasons.LineageMismatch);
            }

            // 10: artifacts
            var artifacts = root.GetProperty("artifacts");
            if (!await ArtifactsValidAsync(artifacts, declared, location, ct))
            {
                return Reject(ResultRejectionReasons.ArtifactInvalid);
            }

            // 11: validity window and fact count
            var validity = root.GetProperty("validityWindow");
            if (!validity.GetProperty("startMs").TryGetInt64(out var startMs)
                || !validity.GetProperty("endMs").TryGetInt64(out var endMs)
                || !root.GetProperty("factCount").TryGetInt32(out var factCount)
                || factCount < 0
                || startMs < 0
                || startMs > endMs
                || (window is { } w && (startMs < w.Start || endMs > w.End)))
            {
                return Reject(ResultRejectionReasons.ValidityInvalid);
            }

            return ResultVerificationOutcome.Accepted(new ValidatedResultManifest(
                SupportedContractVersion,
                root.GetProperty("outputType").GetString()!,
                startMs,
                endMs,
                factCount,
                artifacts.GetRawText(),
                root.GetProperty("upstreamLineage").GetRawText()));
        }
    }

    private static ResultVerificationOutcome Reject(string reason) => ResultVerificationOutcome.Rejected(reason);

    private static bool ScopeMatches(ResultVerificationRequest request, JsonElement root, out (long Start, long End)? segmentWindow)
    {
        segmentWindow = null;
        var scope = root.GetProperty("scope");
        if (!IdEquals(scope, "teamId", request.Run.TeamId)
            || !IdEquals(scope, "matchId", request.Run.MatchId)
            || !IdEquals(scope, "recordingSetVersionId", request.Run.RecordingSetVersionId)
            || !IdEquals(scope, "runId", request.Run.Id)
            || !IdEquals(scope, "nodeId", request.Node.Id)
            || !IdEquals(scope, "logicalJobId", request.Job.Id))
        {
            return false;
        }

        var attempt = root.GetProperty("attempt");
        if (!IdEquals(attempt, "attemptId", request.AttemptId)
            || !attempt.GetProperty("attemptNumber").TryGetInt32(out var number)
            || number != request.AttemptNumber)
        {
            return false;
        }

        if (request.Node.ExecutionScope == ExecutionScope.Match)
        {
            return scope.TryGetProperty("match", out _) && !scope.TryGetProperty("segment", out _);
        }

        if (!scope.TryGetProperty("segment", out var segment) || scope.TryGetProperty("match", out _) || request.Job.SegmentNumber is not { } segmentNumber)
        {
            return false;
        }

        using var snapshot = JsonDocument.Parse(request.RunSnapshotJson);
        var duration = snapshot.RootElement.GetProperty("segmentationPolicy").GetProperty("durationSeconds").GetInt32() * 1000L;
        var start = (segmentNumber - 1) * duration;
        var end = segmentNumber * duration;
        if (!segment.GetProperty("windowStartMs").TryGetInt64(out var reportedStart)
            || !segment.GetProperty("windowEndMs").TryGetInt64(out var reportedEnd)
            || reportedStart != start
            || reportedEnd != end)
        {
            return false;
        }

        segmentWindow = (start, end);
        return true;
    }

    private static bool ProvenanceMatches(ResultVerificationRequest request, JsonElement provenance)
    {
        var node = request.Node;
        var snapshot = request.Snapshot;
        var capability = provenance.GetProperty("capability");
        if (capability.GetProperty("id").GetString() != node.CapabilityId
            || Pad(capability.GetProperty("version").GetString()) != Pad(node.CapabilityVersion)
            || Pad(provenance.GetProperty("schemaVersion").GetString()) != Pad(snapshot.SchemaVersion)
            || provenance.GetProperty("imageDigest").GetString() != snapshot.ImageDigest)
        {
            return false;
        }

        var profile = provenance.GetProperty("analystProfile");
        using var nodeDocument = JsonDocument.Parse(node.SnapshotJson);
        var nodeRoot = nodeDocument.RootElement;
        var profileId = nodeRoot.TryGetProperty("analystProfileId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (profile.GetProperty("id").GetString() != profileId || profile.GetProperty("digest").GetString() != snapshot.AnalystProfileDigest)
        {
            return false;
        }

        var model = provenance.TryGetProperty("modelDigest", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (model != snapshot.ModelDigest)
        {
            return false;
        }

        var workflow = provenance.GetProperty("workflow");
        if (workflow.GetProperty("id").GetString() != request.Run.WorkflowId
            || Pad(workflow.GetProperty("version").GetString()) != Pad(request.Run.WorkflowVersion))
        {
            return false;
        }

        var expectedPolicies = nodeRoot.TryGetProperty("policyVersions", out var pv) && pv.ValueKind == JsonValueKind.Object
            ? pv.EnumerateObject().Select(p => (Id: p.Name, Version: Pad(p.Value.GetString()))).Order().ToList()
            : [];
        var reportedPolicies = provenance.GetProperty("policyVersions").EnumerateArray()
            .Select(p => (Id: p.GetProperty("id").GetString()!, Version: Pad(p.GetProperty("version").GetString()))).Order().ToList();
        return expectedPolicies.SequenceEqual(reportedPolicies);
    }

    private static bool LineageMatches(ResultVerificationRequest request, JsonElement lineage)
    {
        var expected = request.Upstream
            .Select(u => (Id: u.Id.ToString("D", CultureInfo.InvariantCulture), Capability: u.CapabilityId, CapabilityVersion: Pad(u.CapabilityVersion),
                Schema: Pad(u.SchemaVersion), Checksum: u.ManifestChecksum))
            .Order().ToList();
        var reported = lineage.EnumerateArray()
            .Select(e => (
                Id: Guid.TryParse(e.GetProperty("acceptedResultId").GetString(), out var guid) ? guid.ToString("D", CultureInfo.InvariantCulture) : string.Empty,
                Capability: e.GetProperty("capability").GetProperty("id").GetString()!,
                CapabilityVersion: Pad(e.GetProperty("capability").GetProperty("version").GetString()),
                Schema: Pad(e.GetProperty("schemaVersion").GetString()),
                Checksum: e.GetProperty("manifestChecksum").GetString()!))
            .Order().ToList();
        return expected.SequenceEqual(reported);
    }

    private async Task<bool> ArtifactsValidAsync(
        JsonElement artifacts, Dictionary<string, ResultObjectDeclaration> declared, string location, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var name = artifact.GetProperty("name").GetString()!;
            var digest = artifact.GetProperty("digest").GetString();
            if (!names.Add(name)
                || name == IssueResultGrantsHandler.ManifestName
                || !artifact.GetProperty("sizeBytes").TryGetInt64(out var size)
                || !declared.TryGetValue(name, out var declaration)
                || declaration.Digest != digest
                || declaration.SizeBytes != size
                || !Sha256Digest.TryParse(digest, out var parsed))
            {
                return false;
            }

            var attested = await storage.GetObjectChecksumAsync(location + name, ct);
            if (attested is null || !attested.Digest.Equals(parsed) || attested.ContentLength != size)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IdEquals(JsonElement element, string property, Guid expected) =>
        Guid.TryParse(element.GetProperty(property).GetString(), out var actual) && actual == expected;

    // Pads short versions such as "1" or "1.2" to the semantic-version form the contracts require.
    private static string Pad(string? value)
    {
        var parts = (value ?? "0").Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(3));
    }
}
