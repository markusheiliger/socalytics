using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Infrastructure.Contracts;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class ResultManifestVerifierTests(RustFsContainerFixture store)
{
    private static readonly HttpClient Http = new();

    private const string ProfileDigest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImageDigest = "sha-256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string UpstreamChecksum = "sha-256:1111111111111111111111111111111111111111111111111111111111111111";

    private static string DigestOf(byte[] bytes) => Sha256Digest.FromBytes(SHA256.HashData(bytes)).ToString();

    private sealed class InMemoryDeclarations : IResultDeclarationStore
    {
        public List<ResultObjectDeclaration> Items { get; } = [];

        public Task<ResultDeclarationOutcome> DeclareAsync(ResultObjectDeclaration declaration, CancellationToken cancellationToken)
        {
            Items.Add(declaration);
            return Task.FromResult(ResultDeclarationOutcome.Declared);
        }

        public Task<IReadOnlyList<ResultObjectDeclaration>> ListByAttemptAsync(Guid attemptId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResultObjectDeclaration>>(Items.Where(i => i.AttemptId == attemptId).ToList());
    }

    private sealed class Scenario
    {
        public required Guid TeamId { get; init; }
        public required Guid MatchId { get; init; }
        public required Guid SetId { get; init; }
        public required Guid RunId { get; init; }
        public required Guid NodeId { get; init; }
        public required Guid JobId { get; init; }
        public required Guid AttemptId { get; init; }
        public required Guid UpstreamId { get; init; }
        public required bool MatchScope { get; init; }
        public required byte[] Artifact { get; init; }
        public string Location => IssueResultGrantsHandler.ResultLocation(RunId, JobId, AttemptId);
        public bool WithUpstream => MatchScope;
    }

    private static Scenario NewScenario(bool matchScope) => new()
    {
        TeamId = Guid.NewGuid(),
        MatchId = Guid.NewGuid(),
        SetId = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        NodeId = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        UpstreamId = Guid.NewGuid(),
        MatchScope = matchScope,
        Artifact = Encoding.UTF8.GetBytes("{\"facts\":[]}"),
    };

    private static JsonObject BaselineManifest(Scenario s)
    {
        var scope = new JsonObject
        {
            ["teamId"] = s.TeamId.ToString(),
            ["matchId"] = s.MatchId.ToString(),
            ["recordingSetVersionId"] = s.SetId.ToString(),
            ["runId"] = s.RunId.ToString(),
            ["nodeId"] = s.NodeId.ToString(),
            ["logicalJobId"] = s.JobId.ToString(),
        };
        if (s.MatchScope)
        {
            scope["match"] = true;
        }
        else
        {
            scope["segment"] = new JsonObject { ["segmentId"] = "segment-3", ["windowStartMs"] = 600000, ["windowEndMs"] = 900000 };
        }

        return new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["scope"] = scope,
            ["provenance"] = new JsonObject
            {
                ["capability"] = new JsonObject { ["id"] = "player-detection", ["version"] = "1.0.0" },
                ["schemaVersion"] = "1.0.0",
                ["analystProfile"] = new JsonObject { ["id"] = "profile-1", ["digest"] = ProfileDigest },
                ["imageDigest"] = ImageDigest,
                ["policyVersions"] = new JsonArray(new JsonObject { ["id"] = "segmentation", ["version"] = "1.0.0" }),
                ["workflow"] = new JsonObject { ["id"] = "default-workflow", ["version"] = "1.0.0" },
            },
            ["attempt"] = new JsonObject { ["attemptId"] = s.AttemptId.ToString(), ["attemptNumber"] = 1 },
            ["artifacts"] = new JsonArray(new JsonObject
            {
                ["name"] = "facts.json",
                ["digest"] = DigestOf(s.Artifact),
                ["sizeBytes"] = s.Artifact.Length,
                ["mediaType"] = "application/json",
            }),
            ["validityWindow"] = s.MatchScope
                ? new JsonObject { ["startMs"] = 0, ["endMs"] = 5400000 }
                : new JsonObject { ["startMs"] = 600000, ["endMs"] = 900000 },
            ["factCount"] = 0,
            ["upstreamLineage"] = s.WithUpstream
                ? new JsonArray(new JsonObject
                {
                    ["acceptedResultId"] = s.UpstreamId.ToString(),
                    ["capability"] = new JsonObject { ["id"] = "upstream-capability", ["version"] = "1.0.0" },
                    ["schemaVersion"] = "1.0.0",
                    ["manifestChecksum"] = UpstreamChecksum,
                })
                : new JsonArray(),
            ["outputType"] = "foundational-facts",
            ["confidence"] = 1,
            ["evidenceReferences"] = new JsonArray(),
        };
    }

    private async Task PutAsync(string key, byte[] body)
    {
        var grant = store.CreateObjectStorage()
            .PresignPutObject(key, body.Length, Sha256Digest.FromBytes(SHA256.HashData(body)), DateTimeOffset.UtcNow.AddMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentLength = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        (await Http.SendAsync(request, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
    }

    private sealed record Prepared(ResultManifestVerifier Verifier, ResultVerificationRequest Request, InMemoryDeclarations Declarations, byte[] Manifest);

    private async Task<Prepared> PrepareAsync(
        Scenario s,
        Action<JsonObject>? mutate = null,
        bool uploadManifest = true,
        bool declareManifest = true,
        bool uploadArtifact = true,
        string? artifactDeclaredDigest = null,
        string? reportedChecksum = null,
        string? declaredManifestDigest = null,
        long maxManifestBytes = 1048576,
        bool lineageUpstream = true,
        IObjectStorage? storage = null)
    {
        var manifest = BaselineManifest(s);
        mutate?.Invoke(manifest);
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();

        if (uploadManifest)
        {
            await PutAsync(s.Location + "manifest.json", bytes);
        }

        if (uploadArtifact)
        {
            await PutAsync(s.Location + "facts.json", s.Artifact);
        }

        var declarations = new InMemoryDeclarations();
        var now = DateTimeOffset.UtcNow;
        if (declareManifest)
        {
            declarations.Items.Add(new ResultObjectDeclaration(s.AttemptId, "manifest.json", bytes.Length, declaredManifestDigest ?? DigestOf(bytes), now));
        }

        declarations.Items.Add(new ResultObjectDeclaration(s.AttemptId, "facts.json", s.Artifact.Length, artifactDeclaredDigest ?? DigestOf(s.Artifact), now));

        var verifier = new ResultManifestVerifier(
            storage ?? store.CreateObjectStorage(),
            new JsonSchemaContractValidator(),
            declarations,
            Options.Create(new AnalysisResultOptions { MaxManifestBytes = maxManifestBytes }));
        var request = BuildRequest(s, reportedChecksum ?? DigestOf(bytes), s.Location + "manifest.json", lineageUpstream);
        return new Prepared(verifier, request, declarations, bytes);
    }

    private static ResultVerificationRequest BuildRequest(Scenario s, string checksum, string reference, bool lineageUpstream)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AnalysisRunRecord(
            s.RunId, s.TeamId, s.MatchId, s.SetId, "default-workflow", "1", ["player-detection"], "all", AnalysisRunState.Running, null, now, now, null, now, 1);
        var scope = s.MatchScope ? ExecutionScope.Match : ExecutionScope.Segment;
        var node = new WorkflowNodeRecord(
            s.NodeId, "detect", "player-detection", "1", CapabilityTier.LowLevel, scope, 0, WorkflowNodeState.Active, null, now,
            """{"analystProfileId":"profile-1","policyVersions":{"segmentation":"1"}}""");
        var job = new LogicalJobRecord(
            s.JobId, s.RunId, s.NodeId, scope, s.MatchScope ? null : Guid.NewGuid(), null, null, s.MatchScope ? null : 3,
            LogicalJobState.Claimed, 1, null, s.AttemptId, null, now, 1);
        var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, ProfileDigest, ImageDigest, null, "{}", "1");
        var upstream = s.WithUpstream && lineageUpstream
            ? new List<AcceptedResultRecord>
            {
                new(s.UpstreamId, Guid.NewGuid(), s.RunId, Guid.NewGuid(), Guid.NewGuid(), "results/x/manifest.json", UpstreamChecksum,
                    "upstream-capability", "1", "1", ProfileDigest, ImageDigest, null, "{}", "{}", [], now),
            }
            : [];
        return new ResultVerificationRequest(
            run, """{"segmentationPolicy":{"durationSeconds":300}}""", node, job, snapshot, s.AttemptId, 1, reference, checksum, upstream);
    }

    private static async Task ShouldRejectAsync(Prepared p, string reason)
    {
        var outcome = await p.Verifier.VerifyAsync(p.Request, TestContext.Current.CancellationToken);
        outcome.Status.ShouldBe(ResultVerificationStatus.Rejected);
        outcome.Reason.ShouldBe(reason);
        outcome.Manifest.ShouldBeNull();
    }

    [Fact]
    public async Task Valid_segment_manifest_is_accepted()
    {
        var p = await PrepareAsync(NewScenario(matchScope: false));

        var outcome = await p.Verifier.VerifyAsync(p.Request, TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(ResultVerificationStatus.Valid);
        outcome.Manifest.ShouldNotBeNull();
        outcome.Manifest.OutputType.ShouldBe("foundational-facts");
        outcome.Manifest.ValidityStartMs.ShouldBe(600000);
        outcome.Manifest.ValidityEndMs.ShouldBe(900000);
        outcome.Manifest.FactCount.ShouldBe(0);
    }

    [Fact]
    public async Task Valid_match_manifest_with_lineage_is_accepted()
    {
        var p = await PrepareAsync(NewScenario(matchScope: true));

        var outcome = await p.Verifier.VerifyAsync(p.Request, TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(ResultVerificationStatus.Valid);
        outcome.Manifest!.UpstreamLineageJson.ShouldContain(UpstreamChecksum);
    }

    [Fact]
    public async Task Check_1_reference_outside_the_result_location_is_missing()
    {
        var s = NewScenario(false);
        var p = await PrepareAsync(s);
        var request = p.Request with { ManifestReference = $"results/{s.RunId}/{s.JobId}/{Guid.NewGuid()}/manifest.json" };

        await ShouldRejectAsync(p with { Request = request }, "result-manifest-missing");
    }

    [Fact]
    public async Task Check_1_undeclared_manifest_is_missing()
    {
        var p = await PrepareAsync(NewScenario(false), declareManifest: false);

        await ShouldRejectAsync(p, "result-manifest-missing");
    }

    [Fact]
    public async Task Check_2_absent_manifest_object_is_missing()
    {
        var p = await PrepareAsync(NewScenario(false), uploadManifest: false);

        await ShouldRejectAsync(p, "result-manifest-missing");
    }

    [Fact]
    public async Task Check_3_oversize_manifest_is_too_large()
    {
        var p = await PrepareAsync(
            NewScenario(false),
            m => m["evidenceReferences"] = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode)JsonValue.Create(new string('e', 30) + i)!).ToArray()),
            maxManifestBytes: 1024);

        await ShouldRejectAsync(p, "result-manifest-too-large");
    }

    [Fact]
    public async Task Check_4_reported_checksum_differing_from_the_object_is_a_mismatch()
    {
        var p = await PrepareAsync(NewScenario(false), reportedChecksum: UpstreamChecksum);

        await ShouldRejectAsync(p, "result-manifest-checksum-mismatch");
    }

    [Fact]
    public async Task Check_4_declared_digest_differing_from_the_object_is_a_mismatch()
    {
        var p = await PrepareAsync(NewScenario(false), declaredManifestDigest: UpstreamChecksum);

        await ShouldRejectAsync(p, "result-manifest-checksum-mismatch");
    }

    [Fact]
    public async Task Check_4_storage_that_cannot_attest_a_checksum_fails_closed()
    {
        var p = await PrepareAsync(NewScenario(false), storage: new NoChecksumStorage(store.CreateObjectStorage()));

        await ShouldRejectAsync(p, "result-manifest-checksum-mismatch");
    }

    [Fact]
    public async Task Unavailable_storage_is_reported_without_a_rejection()
    {
        var p = await PrepareAsync(NewScenario(false), storage: new UnavailableStorage(store.CreateObjectStorage()));

        var outcome = await p.Verifier.VerifyAsync(p.Request, TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(ResultVerificationStatus.StorageUnavailable);
        outcome.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Check_5_unsupported_contract_version()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["contractVersion"] = "1.1.0");

        await ShouldRejectAsync(p, "result-manifest-version-unsupported");
    }

    [Fact]
    public async Task Check_6_schema_violation()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["unexpected"] = "value");

        await ShouldRejectAsync(p, "result-manifest-schema-invalid");
    }

    [Fact]
    public async Task Check_7_foreign_team_is_a_scope_mismatch()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["scope"]!["teamId"] = Guid.NewGuid().ToString());

        await ShouldRejectAsync(p, "result-scope-mismatch");
    }

    [Fact]
    public async Task Check_7_other_attempt_is_a_scope_mismatch()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["attempt"]!["attemptId"] = Guid.NewGuid().ToString());

        await ShouldRejectAsync(p, "result-scope-mismatch");
    }

    [Fact]
    public async Task Check_7_wrong_segment_window_is_a_scope_mismatch()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["scope"]!["segment"]!["windowEndMs"] = 910000);

        await ShouldRejectAsync(p, "result-scope-mismatch");
    }

    [Fact]
    public async Task Check_8_other_image_is_a_provenance_mismatch()
    {
        var p = await PrepareAsync(
            NewScenario(false), m => m["provenance"]!["imageDigest"] = "sha-256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

        await ShouldRejectAsync(p, "result-provenance-mismatch");
    }

    [Fact]
    public async Task Check_9_missing_upstream_is_a_lineage_mismatch()
    {
        var p = await PrepareAsync(NewScenario(true), m => m["upstreamLineage"] = new JsonArray());

        await ShouldRejectAsync(p, "result-lineage-mismatch");
    }

    [Fact]
    public async Task Check_9_other_upstream_checksum_is_a_lineage_mismatch()
    {
        var p = await PrepareAsync(
            NewScenario(true),
            m => m["upstreamLineage"]![0]!["manifestChecksum"] = "sha-256:2222222222222222222222222222222222222222222222222222222222222222");

        await ShouldRejectAsync(p, "result-lineage-mismatch");
    }

    [Fact]
    public async Task Check_10_artifact_that_was_never_stored_is_invalid()
    {
        var p = await PrepareAsync(NewScenario(false), uploadArtifact: false);

        await ShouldRejectAsync(p, "result-artifact-invalid");
    }

    [Fact]
    public async Task Check_10_artifact_declared_with_another_digest_is_invalid()
    {
        var p = await PrepareAsync(NewScenario(false), artifactDeclaredDigest: UpstreamChecksum);

        await ShouldRejectAsync(p, "result-artifact-invalid");
    }

    [Fact]
    public async Task Check_10_undeclared_artifact_name_is_invalid()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["artifacts"]![0]!["name"] = "other.json");

        await ShouldRejectAsync(p, "result-artifact-invalid");
    }

    [Fact]
    public async Task Check_11_validity_window_outside_the_segment_is_invalid()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["validityWindow"]!["endMs"] = 900001);

        await ShouldRejectAsync(p, "result-validity-invalid");
    }

    [Fact]
    public async Task Check_11_inverted_validity_window_is_invalid()
    {
        var p = await PrepareAsync(NewScenario(false), m => m["validityWindow"] = new JsonObject { ["startMs"] = 800000, ["endMs"] = 700000 });

        await ShouldRejectAsync(p, "result-validity-invalid");
    }

    private class DelegatingStorage(IObjectStorage inner) : IObjectStorage
    {
        public virtual Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) =>
            inner.InitiateCompositeMultipartUploadAsync(objectKey, contentType, cancellationToken);

        public virtual PartUploadGrant PresignUploadPart(MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt) =>
            inner.PresignUploadPart(upload, partNumber, contentLength, partDigest, expiresAt);

        public virtual Task<IReadOnlyList<StoredPart>> ListPartsAsync(MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken) =>
            inner.ListPartsAsync(upload, maxParts, cancellationToken);

        public virtual Task CompleteMultipartUploadAsync(
            MultipartUploadReference upload, IReadOnlyList<CompletedPartEntry> parts, CompositeContentDigest contentDigest, long totalSizeBytes, CancellationToken cancellationToken) =>
            inner.CompleteMultipartUploadAsync(upload, parts, contentDigest, totalSizeBytes, cancellationToken);

        public virtual Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.GetIntegrityEvidenceAsync(objectKey, cancellationToken);

        public virtual ObjectUploadGrant PresignPutObject(string objectKey, long contentLength, Sha256Digest digest, DateTimeOffset expiresAt) =>
            inner.PresignPutObject(objectKey, contentLength, digest, expiresAt);

        public virtual ObjectReadGrant PresignGetObject(string objectKey, DateTimeOffset expiresAt) =>
            inner.PresignGetObject(objectKey, expiresAt);

        public virtual Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.GetObjectChecksumAsync(objectKey, cancellationToken);

        public virtual Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadObjectAsync(objectKey, maxBytes, cancellationToken);

        public virtual Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken) =>
            inner.AbortMultipartUploadAsync(upload, cancellationToken);

        public virtual Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.DeleteObjectAsync(objectKey, cancellationToken);
    }

    private sealed class NoChecksumStorage(IObjectStorage inner) : DelegatingStorage(inner)
    {
        public override Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectChecksum?>(null);
    }

    private sealed class UnavailableStorage(IObjectStorage inner) : DelegatingStorage(inner)
    {
        public override Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
            throw new ObjectStorageUnavailableException();
    }
}
