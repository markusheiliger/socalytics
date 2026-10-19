using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using FieldViolation = SocAlytics.Platform.Application.Abstractions.FieldViolation;

namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed record ListUpstreamResultsQuery(
    Guid AttemptId,
    long? FencingToken,
    string? CapabilityId,
    string? CapabilityVersion,
    string? SchemaVersion,
    IReadOnlyList<string>? SegmentIds,
    long? MatchStartMs,
    long? MatchEndMs,
    int? PageSize,
    string? ContinuationToken);

public sealed record UpstreamReadGrant(string Name, Uri Url, DateTimeOffset ExpiresAt);

public sealed record UpstreamResultView(
    Guid AcceptedResultId,
    string? SegmentId,
    long? WindowStartMs,
    long? WindowEndMs,
    string ManifestChecksum,
    JsonElement Manifest,
    IReadOnlyList<UpstreamReadGrant> Grants);

public sealed record ListUpstreamResultsResult(
    Guid RunId,
    Guid LogicalJobId,
    string CapabilityId,
    string CapabilityVersion,
    string SchemaVersion,
    string EdgeKind,
    IReadOnlyList<UpstreamResultView> Results,
    IReadOnlyList<CoverageEntry> Coverage,
    string? ContinuationToken,
    CoverageMode CoverageMode = CoverageMode.Complete,
    decimal? MinimumAcceptedFraction = null);

public sealed class ListUpstreamResultsHandler(
    IUnitOfWork unitOfWork,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IAnalysisRunStore runs,
    IUpstreamResultReader reader,
    IObjectStorage storage,
    IAuditTrail audit,
    ICurrentAnalystManager manager,
    IOptions<AnalysisReadOptions> readOptions,
    IOptions<AnalysisResultOptions> resultOptions,
    TimeProvider time)
{
    public const string UpstreamDependencyUnknownCode = "upstream-dependency-unknown";
    public const string AttemptObsoleteCode = IssueResultGrantsHandler.AttemptObsoleteCode;

    private sealed record Scoped(
        Guid RunId, Guid JobId, string CapabilityId, string CapabilityVersion, string SchemaVersion, string EdgeKind,
        UpstreamResultsPage Page, DateTimeOffset GrantsExpireAt);

    public async Task<OperationResult<ListUpstreamResultsResult>> HandleAsync(ListUpstreamResultsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var reads = readOptions.Value;

        Guid? refusedRunId = null;
        var refusedJobId = Guid.Empty;
        string? refusalReason = null;
        OperationFailure? failure = null;
        Scoped? scoped = null;

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var attempt = await attempts.LockAsync(query.AttemptId, cancellationToken);
            if (attempt is null)
            {
                return OperationFailure.NotFound();
            }

            var job = await jobs.LockJobAsync(attempt.LogicalJobId, cancellationToken);
            if (job is null)
            {
                return OperationFailure.NotFound();
            }

            if (attempt.ManagerId != manager.ManagerId)
            {
                refusedRunId = job.RunId;
                refusedJobId = job.Id;
                refusalReason = AnalysisAuditEvents.ForeignAttemptReason;
                failure = OperationFailure.Forbidden();
            }
            else
            {
                var now = time.GetUtcNow();
                if (query.FencingToken is not { } token
                    || attempt.State != ExecutionAttemptState.Active
                    || attempt.LeaseExpiresAt <= now
                    || attempt.FencingToken != token
                    || job.CurrentAttemptId != attempt.Id)
                {
                    return query.FencingToken is null
                        ? OperationFailure.Validation(new FieldViolation("fencingToken", "required"))
                        : OperationFailure.Conflict(AttemptObsoleteCode);
                }

                var violations = Validate(query, reads.MaxPageSize);
                if (violations.Count > 0)
                {
                    return OperationFailure.Validation(violations.ToArray());
                }

                var hierarchy = await runs.LoadAsync(job.RunId, cancellationToken)
                    ?? throw new InvalidOperationException("The analysis run does not exist.");
                var nodesById = hierarchy.Nodes.ToDictionary(n => n.Id);
                var upstream = hierarchy.Edges
                    .Where(e => e.DependentNodeId == job.NodeId)
                    .Select(e => (Node: nodesById[e.UpstreamNodeId], e.Kind))
                    .FirstOrDefault(u => u.Node.CapabilityId == query.CapabilityId
                        && Pad(u.Node.CapabilityVersion) == Pad(query.CapabilityVersion)
                        && Pad(NodeSchemaVersion(u.Node)) == Pad(query.SchemaVersion));
                if (upstream.Node is null)
                {
                    refusedRunId = job.RunId;
                    refusedJobId = job.Id;
                    refusalReason = AnalysisAuditEvents.OutOfScopeReason;
                    failure = OperationFailure.Validation(UpstreamDependencyUnknownCode);
                }
                else
                {
                    UpstreamResultsPage? page;
                    try
                    {
                        page = await reader.ReadAsync(
                            new UpstreamReadRequest(
                                job.Id, upstream.Node.NodeKey, query.SegmentIds, query.MatchStartMs, query.MatchEndMs,
                                query.PageSize ?? reads.MaxPageSize, query.ContinuationToken),
                            cancellationToken);
                    }
                    catch (InvalidContinuationTokenException)
                    {
                        return OperationFailure.Validation(new FieldViolation("continuationToken", "invalid"));
                    }

                    if (page is null)
                    {
                        refusedRunId = job.RunId;
                        refusedJobId = job.Id;
                        refusalReason = AnalysisAuditEvents.OutOfScopeReason;
                        failure = OperationFailure.Validation(UpstreamDependencyUnknownCode);
                    }
                    else
                    {
                        var lifetimeEnd = now + reads.GrantLifetime;
                        scoped = new Scoped(
                            job.RunId, job.Id, upstream.Node.CapabilityId, upstream.Node.CapabilityVersion, NodeSchemaVersion(upstream.Node),
                            upstream.Kind.ToString().ToLowerInvariant(), page,
                            lifetimeEnd <= attempt.LeaseExpiresAt ? lifetimeEnd : attempt.LeaseExpiresAt);
                    }
                }
            }
        }

        if (scoped is null)
        {
            await audit.RecordIndependentAsync(
                AnalysisAuditEvents.UpstreamReadRefused(query.AttemptId, refusedJobId, refusedRunId!.Value, manager.ManagerId, refusalReason!),
                cancellationToken);
            return failure!;
        }

        var results = new List<UpstreamResultView>(scoped.Page.Results.Count);
        var grantCount = 0;
        foreach (var item in scoped.Page.Results)
        {
            var (manifest, grants) = await ReadAndGrantAsync(item, scoped.GrantsExpireAt, cancellationToken);
            grantCount += grants.Count;
            results.Add(new UpstreamResultView(
                item.AcceptedResultId, item.SegmentId, item.WindowStartMs, item.WindowEndMs, item.ManifestChecksum, manifest, grants));
        }

        AnalysisMetrics.ReadGrantsIssued(grantCount);
        return new ListUpstreamResultsResult(
            scoped.RunId, scoped.JobId, scoped.CapabilityId, scoped.CapabilityVersion, scoped.SchemaVersion, scoped.EdgeKind,
            results, scoped.Page.Coverage, scoped.Page.ContinuationToken,
            scoped.Page.CoverageMode, scoped.Page.MinimumAcceptedFraction);
    }

    private async Task<(JsonElement Manifest, List<UpstreamReadGrant> Grants)> ReadAndGrantAsync(
        UpstreamResultItem item, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var key = item.ManifestReference;
        var read = await storage.ReadObjectAsync(key, resultOptions.Value.MaxManifestBytes, ct);
        if (read?.Content is null
            || !key.EndsWith(IssueResultGrantsHandler.ManifestName, StringComparison.Ordinal)
            || !Sha256Digest.TryParse(item.ManifestChecksum, out var expected)
            || !Sha256Digest.FromBytes(SHA256.HashData(read.Content)).Equals(expected))
        {
            // A missing, oversized, or altered manifest cannot be served; the read behaves as unavailable storage.
            throw new ObjectStorageUnavailableException();
        }

        var location = key[..^IssueResultGrantsHandler.ManifestName.Length];
        using var document = JsonDocument.Parse(read.Content);
        var manifest = document.RootElement.Clone();

        var names = new List<string> { IssueResultGrantsHandler.ManifestName };
        using var artifacts = JsonDocument.Parse(item.Manifest.ArtifactsJson);
        names.AddRange(artifacts.RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString()!));

        var grants = names
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var grant = storage.PresignGetObject(location + name, expiresAt);
                return new UpstreamReadGrant(name, grant.Url, grant.ExpiresAt);
            })
            .ToList();
        return (manifest, grants);
    }

    private static List<FieldViolation> Validate(ListUpstreamResultsQuery q, int maxPageSize)
    {
        var violations = new List<FieldViolation>();
        if (q.FencingToken is <= 0)
        {
            violations.Add(new FieldViolation("fencingToken", "minimum"));
        }

        Require(violations, "capabilityId", q.CapabilityId);
        Require(violations, "capabilityVersion", q.CapabilityVersion);
        Require(violations, "schemaVersion", q.SchemaVersion);

        var hasSegments = q.SegmentIds is { Count: > 0 };
        var hasRange = q.MatchStartMs is not null || q.MatchEndMs is not null;
        if (hasSegments && hasRange)
        {
            violations.Add(new FieldViolation("segmentIds", "mutuallyExclusive"));
        }

        if (hasRange)
        {
            if (q.MatchStartMs is null || q.MatchEndMs is null)
            {
                violations.Add(new FieldViolation(q.MatchStartMs is null ? "matchStartMs" : "matchEndMs", "required"));
            }
            else if (q.MatchStartMs < 0 || q.MatchEndMs <= q.MatchStartMs)
            {
                violations.Add(new FieldViolation("matchEndMs", "range"));
            }
        }

        if (q.PageSize is { } size && (size < 1 || size > maxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", size < 1 ? "minimum" : "maximum"));
        }

        return violations;
    }

    private static void Require(List<FieldViolation> violations, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(new FieldViolation(field, "required"));
        }
    }

    private static string NodeSchemaVersion(WorkflowNodeRecord node)
    {
        using var document = JsonDocument.Parse(node.SnapshotJson);
        return document.RootElement.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : string.Empty;
    }

    private static string Pad(string? value)
    {
        var parts = (value ?? "0").Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(3).Select(p => p.ToString(CultureInfo.InvariantCulture)));
    }
}
