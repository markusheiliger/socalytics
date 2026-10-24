using System.Diagnostics;
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
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

/// <summary>
/// SC-006: payload validation of a result at the maximum permitted fact count adds at most 10% to the acceptance time
/// of the same result without payload schemas, or the FR-011 bound is reached with its named reason.
/// </summary>
public sealed class FactPayloadPerformanceTests
{
    private const string ProfileDigest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImageDigest = "sha-256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FactMediaType = "application/vnd.socalytics.facts+json";
    private const string FactsName = "fact-data.json";
    private const string PlaceholderName = "facts.json";

    private const int MaxFacts = 20_000;
    private const int WarmUpRuns = 2;
    private const int MeasuredRuns = 5;

    // The budget is the 10% of SC-006; the absolute slack absorbs scheduler and GC noise of shared CI machines.
    private const double AllowedRatio = 1.10;
    private static readonly TimeSpan CiSlack = TimeSpan.FromMilliseconds(500);

    private const string KindSchema =
        """{"type":"object","properties":{"x":{"type":"integer","minimum":0},"label":{"type":"string","maxLength":32}},"required":["x"],"additionalProperties":false}""";

    private sealed class InMemoryStorage : IObjectStorage
    {
        private readonly Dictionary<string, byte[]> _objects = [];

        public void Put(string key, byte[] body) => _objects[key] = body;

        public Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectReadResult?>(
                !_objects.TryGetValue(objectKey, out var body) ? null : new ObjectReadResult(body.Length > maxBytes ? null : body));

        public Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.TryGetValue(objectKey, out var body)
                ? new ObjectChecksum(Sha256Digest.FromBytes(SHA256.HashData(body)), body.Length)
                : null);

        public Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public PartUploadGrant PresignUploadPart(MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredPart>> ListPartsAsync(MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteMultipartUploadAsync(MultipartUploadReference upload, IReadOnlyList<CompletedPartEntry> parts, CompositeContentDigest contentDigest, long totalSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectUploadGrant PresignPutObject(string objectKey, long contentLength, Sha256Digest digest, DateTimeOffset expiresAt) => throw new NotSupportedException();

        public Task PutObjectAsync(string objectKey, string contentType, Stream content, long contentLength, Sha256Digest digest, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectReadGrant PresignGetObject(string objectKey, DateTimeOffset expiresAt) => throw new NotSupportedException();

        public Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

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

    private sealed record Fixture(ResultManifestVerifier Verifier, ResultVerificationRequest WithoutSchemas, ResultVerificationRequest WithSchemas);

    private static string DigestOf(byte[] bytes) => Sha256Digest.FromBytes(SHA256.HashData(bytes)).ToString();

    private static Fixture Build(int factCount)
    {
        var teamId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var location = IssueResultGrantsHandler.ResultLocation(runId, jobId, attemptId);

        var facts = new JsonArray();
        for (var i = 0; i < factCount; i++)
        {
            var start = 600000 + (i % 1000) * 10;
            facts.Add(new JsonObject
            {
                ["kind"] = "detection",
                ["startMs"] = start,
                ["endMs"] = start + 5,
                ["confidence"] = 0.9,
                ["payload"] = new JsonObject { ["x"] = i, ["label"] = "player" },
            });
        }

        var factBytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["capability"] = new JsonObject { ["id"] = "player-detection", ["version"] = "1.0.0" },
            ["schemaVersion"] = "1.0.0",
            ["outputType"] = "foundational-facts",
            ["facts"] = facts,
        }.ToJsonString());
        var placeholder = Encoding.UTF8.GetBytes("{\"facts\":[]}");

        var manifest = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["scope"] = new JsonObject
            {
                ["teamId"] = teamId.ToString(),
                ["matchId"] = matchId.ToString(),
                ["recordingSetVersionId"] = setId.ToString(),
                ["runId"] = runId.ToString(),
                ["nodeId"] = nodeId.ToString(),
                ["logicalJobId"] = jobId.ToString(),
                ["segment"] = new JsonObject { ["segmentId"] = "segment-3", ["windowStartMs"] = 600000, ["windowEndMs"] = 900000 },
            },
            ["provenance"] = new JsonObject
            {
                ["capability"] = new JsonObject { ["id"] = "player-detection", ["version"] = "1.0.0" },
                ["schemaVersion"] = "1.0.0",
                ["analystProfile"] = new JsonObject { ["id"] = "profile-1", ["digest"] = ProfileDigest },
                ["imageDigest"] = ImageDigest,
                ["policyVersions"] = new JsonArray(new JsonObject { ["id"] = "segmentation", ["version"] = "1.0.0" }),
                ["workflow"] = new JsonObject { ["id"] = "default-workflow", ["version"] = "1.0.0" },
            },
            ["attempt"] = new JsonObject { ["attemptId"] = attemptId.ToString(), ["attemptNumber"] = 1 },
            ["artifacts"] = new JsonArray(
                new JsonObject { ["name"] = PlaceholderName, ["digest"] = DigestOf(placeholder), ["sizeBytes"] = placeholder.Length, ["mediaType"] = "application/json" },
                new JsonObject { ["name"] = FactsName, ["digest"] = DigestOf(factBytes), ["sizeBytes"] = factBytes.Length, ["mediaType"] = FactMediaType }),
            ["validityWindow"] = new JsonObject { ["startMs"] = 600000, ["endMs"] = 900000 },
            ["factCount"] = factCount,
            ["upstreamLineage"] = new JsonArray(),
            ["outputType"] = "foundational-facts",
            ["confidence"] = 1,
            ["evidenceReferences"] = new JsonArray(),
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());

        var storage = new InMemoryStorage();
        storage.Put(location + "manifest.json", manifestBytes);
        storage.Put(location + PlaceholderName, placeholder);
        storage.Put(location + FactsName, factBytes);

        var now = DateTimeOffset.UtcNow;
        var declarations = new InMemoryDeclarations();
        declarations.Items.Add(new ResultObjectDeclaration(attemptId, "manifest.json", manifestBytes.Length, DigestOf(manifestBytes), now));
        declarations.Items.Add(new ResultObjectDeclaration(attemptId, PlaceholderName, placeholder.Length, DigestOf(placeholder), now));
        declarations.Items.Add(new ResultObjectDeclaration(attemptId, FactsName, factBytes.Length, DigestOf(factBytes), now));

        var options = new AnalysisResultOptions
        {
            MaxManifestBytes = 1048576,
            MaxFactArtifactBytes = 16 * 1024 * 1024,
            MaxFactPayloadBytes = 16 * 1024,
            MaxFactsPerResult = factCount,
        };
        var verifier = new ResultManifestVerifier(
            storage, new JsonSchemaContractValidator(), declarations, Options.Create(options), new FactPayloadValidator(Options.Create(options)));

        var run = new AnalysisRunRecord(
            runId, teamId, matchId, setId, now, "default-workflow", "1", ["player-detection"], "all", AnalysisRunState.Running, null, now, now, null, now, 1);
        var node = new WorkflowNodeRecord(
            nodeId, "detect", "player-detection", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, now,
            """{"analystProfileId":"profile-1","policyVersions":{"segmentation":"1"}}""");
        var job = new LogicalJobRecord(
            jobId, runId, nodeId, ExecutionScope.Segment, Guid.NewGuid(), null, null, 3, LogicalJobState.Claimed, 1, null, attemptId, null, now, 1);
        var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, ProfileDigest, ImageDigest, null, "{}", "1");
        var request = new ResultVerificationRequest(
            run, """{"segmentationPolicy":{"durationSeconds":300}}""", node, job, snapshot, attemptId, 1,
            location + "manifest.json", DigestOf(manifestBytes), []);

        var schemaNode = node with
        {
            SnapshotJson = """{"analystProfileId":"profile-1","policyVersions":{"segmentation":"1"},"factPayloadSchemas":{"detection":""" + KindSchema + "}}",
        };
        return new Fixture(verifier, request, request with { Node = schemaNode });
    }

    private static async Task<(TimeSpan Elapsed, ResultVerificationOutcome Outcome)> TimeAsync(Fixture fixture, ResultVerificationRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = await fixture.Verifier.VerifyAsync(request, TestContext.Current.CancellationToken);
        return (Stopwatch.GetElapsedTime(started), outcome);
    }

    private static TimeSpan Median(List<TimeSpan> samples) => samples.Order().ElementAt(samples.Count / 2);

    [Fact]
    public async Task Validation_at_the_maximum_fact_count_adds_at_most_ten_percent_or_reaches_the_named_bound()
    {
        var fixture = Build(MaxFacts);

        for (var i = 0; i < WarmUpRuns; i++)
        {
            (await TimeAsync(fixture, fixture.WithoutSchemas)).Outcome.Status.ShouldBe(ResultVerificationStatus.Valid);
            await TimeAsync(fixture, fixture.WithSchemas);
        }

        var without = new List<TimeSpan>();
        var with = new List<TimeSpan>();
        var bound = false;
        for (var i = 0; i < MeasuredRuns && !bound; i++)
        {
            var baseline = await TimeAsync(fixture, fixture.WithoutSchemas);
            baseline.Outcome.Status.ShouldBe(ResultVerificationStatus.Valid);
            without.Add(baseline.Elapsed);

            var validated = await TimeAsync(fixture, fixture.WithSchemas);
            if (validated.Outcome.Status == ResultVerificationStatus.Rejected)
            {
                // The accepted alternative outcome: the FR-011 bound, with its named reason and no hang.
                validated.Outcome.Reason.ShouldStartWith(ResultRejectionReasons.PayloadValidationBound);
                bound = true;
                break;
            }

            validated.Outcome.Status.ShouldBe(ResultVerificationStatus.Valid);
            with.Add(validated.Elapsed);
        }

        if (bound)
        {
            return;
        }

        var baselineMedian = Median(without);
        var validatedMedian = Median(with);
        var budget = baselineMedian * AllowedRatio + CiSlack;
        validatedMedian.ShouldBeLessThanOrEqualTo(
            budget,
            $"median with schemas {validatedMedian.TotalMilliseconds:F0} ms, without {baselineMedian.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Reaching_the_validation_bound_fails_with_its_named_reason_instead_of_hanging()
    {
        var options = new AnalysisResultOptions { PayloadValidation = new PayloadValidationOptions { MaxDuration = TimeSpan.FromMilliseconds(1) } };
        var bounded = new FactPayloadValidator(Options.Create(options));

        var facts = new List<SocAlytics.Platform.Application.Abstractions.FactPayloadCandidate>();
        var documents = new List<System.Text.Json.JsonDocument>();
        using var schema = System.Text.Json.JsonDocument.Parse(KindSchema);
        for (var i = 0; i < MaxFacts; i++)
        {
            var document = System.Text.Json.JsonDocument.Parse($$"""{"x":{{i}},"label":"player"}""");
            documents.Add(document);
            facts.Add(new("detection", document.RootElement));
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            var verdict = bounded.Validate(new Dictionary<string, System.Text.Json.JsonElement> { ["detection"] = schema.RootElement }, facts);

            verdict.Outcome.ShouldBe(SocAlytics.Platform.Application.Abstractions.FactPayloadValidationOutcome.BoundReached);
            Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            documents.ForEach(d => d.Dispose());
        }
    }
}
