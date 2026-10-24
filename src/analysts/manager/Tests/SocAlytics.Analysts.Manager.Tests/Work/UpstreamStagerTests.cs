#pragma warning disable xUnit1051 // The fake platform and storage answer synchronously; there is nothing to cancel.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

/// <summary>User Story 2 at the Manager level: upstream results are staged, verified, and bounded before the Analyst launches.</summary>
public sealed class UpstreamStagerTests : IDisposable
{
    private const string Upstream = "dev-segment-capability";
    private const string Downstream = "dev-match-capability";
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid OccurrenceId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly Guid _attemptId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly FakeStorage _storage = new();
    private readonly SimulatedAnalystOptions _simulated = new();
    private readonly SimulatedRuntimeAdapter _runtime;
    private readonly PlatformSession _session;
    private readonly StagingOptions _options = new();
    private readonly PlatformClient _client;
    private readonly Queue<string> _pages = new();
    private string _lastPage = string.Empty;
    private Func<string, long>? _freeSpace;

    public UpstreamStagerTests()
    {
        _platform = new FakePlatform(_time);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _runtime = new SimulatedRuntimeAdapter(_simulated, _time);
        _platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","readinessOccurrenceId":"{{OccurrenceId}}","attemptNumber":1,"fencingToken":7,"leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}"""));
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{_attemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));
        _platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
        _platform.Script(WorkOperation.UpstreamRead, () => Json(HttpStatusCode.OK, _pages.Count > 0 ? _lastPage = _pages.Dequeue() : _lastPage));
    }

    public void Dispose()
    {
        var directory = UpstreamStager.DirectoryFor(_attemptId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        if (Directory.Exists(_snapshot))
        {
            Directory.Delete(_snapshot, true);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Stored(Guid Id, string SegmentId, byte[] Facts, byte[] Manifest, long Declared);

    private Stored AddResult(string segmentId, string facts = "{\"facts\":[1,2,3]}", long? declaredSize = null)
    {
        var id = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(facts);
        var size = declaredSize ?? bytes.Length;
        var manifest = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["artifacts"] = new JsonArray(new JsonObject
            {
                ["name"] = "facts.json", ["digest"] = Digest(bytes), ["sizeBytes"] = size, ["mediaType"] = "application/json",
            }),
        }.ToJsonString());
        _storage.Objects[$"/{id}/manifest.json"] = manifest;
        _storage.Objects[$"/{id}/facts.json"] = bytes;
        return new Stored(id, segmentId, bytes, manifest, size);
    }

    private static string Digest(byte[] bytes) => "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Page(IEnumerable<Stored> results, string? next, string coverage, string version = "1.0.0", string? policy = null)
    {
        var items = new JsonArray();
        var index = 0;
        foreach (var r in results)
        {
            var manifestBytes = _storage.Objects[$"/{r.Id}/manifest.json"];
            JsonObject Grant(string name) => new()
            {
                ["name"] = name,
                ["url"] = $"https://storage.test/{r.Id}/{name}?sig=abc",
                ["expiresAt"] = "2026-10-12T14:10:00Z",
            };
            items.Add(new JsonObject
            {
                ["acceptedResultId"] = r.Id.ToString(),
                ["scope"] = new JsonObject
                {
                    ["segment"] = new JsonObject { ["segmentId"] = r.SegmentId, ["windowStartMs"] = index * 1000, ["windowEndMs"] = index * 1000 + 1000 },
                },
                ["manifestChecksum"] = Digest(manifestBytes),
                ["manifest"] = JsonNode.Parse(manifestBytes),
                ["grants"] = new JsonArray(Grant("manifest.json"), Grant("facts.json")),
            });
            index++;
        }

        var upstream = new JsonObject
        {
            ["capability"] = new JsonObject { ["id"] = Upstream, ["version"] = "1.0.0" },
            ["schemaVersion"] = "1.0.0",
            ["edgeKind"] = "required",
        };
        if (policy is not null)
        {
            upstream["coverage"] = JsonNode.Parse(policy);
        }

        return new JsonObject
        {
            ["contractVersion"] = version,
            ["upstream"] = upstream,
            ["results"] = items,
            ["coverage"] = JsonNode.Parse($"[{coverage}]"),
            ["continuationToken"] = next,
        }.ToJsonString();
    }

    private static string Accepted(params Stored[] results) =>
        string.Join(',', results.Select(r => $$"""{"segmentId":"{{r.SegmentId}}","state":"accepted"}"""));

    private InMemoryDelivery Delivery(bool withUpstream = true) => new(_platform, new JsonObject
    {
        ["contractVersion"] = "1.0.0",
        ["messageId"] = MessageId.ToString(),
        ["readinessOccurrenceId"] = OccurrenceId.ToString(),
        ["runId"] = "run-1",
        ["nodeId"] = "node-1",
        ["logicalJobId"] = JobId.ToString(),
        ["teamId"] = "team-1",
        ["matchId"] = "match-1",
        ["capability"] = new JsonObject { ["id"] = Downstream, ["version"] = "1.0.0" },
        ["schemaVersion"] = "1.0.0",
        ["executionScope"] = "match",
        ["inputs"] = withUpstream
            ? new JsonArray(new JsonObject
            {
                ["upstreamNodeKey"] = "detect",
                ["capability"] = new JsonObject { ["id"] = Upstream, ["version"] = "1.0.0" },
                ["edgeKind"] = "required",
                ["status"] = "accepted",
                ["acceptedResults"] = new JsonArray(new JsonObject
                {
                    ["acceptedResultId"] = Guid.NewGuid().ToString(),
                    ["capability"] = new JsonObject { ["id"] = Upstream, ["version"] = "1.0.0" },
                    ["schemaVersion"] = "1.0.0",
                    ["manifestChecksum"] = "sha-256:" + new string('1', 64),
                }),
            })
            : new JsonArray(),
        ["pinned"] = new JsonObject
        {
            ["analystProfileDigest"] = "sha-256:" + new string('3', 64),
            ["imageDigest"] = "sha-256:" + new string('4', 64),
            ["modelDigest"] = "sha-256:" + new string('5', 64),
        },
        ["resources"] = new JsonObject { ["cpuMillicores"] = 1000, ["memoryMebibytes"] = 2048 },
        ["correlation"] = new JsonObject { ["correlationId"] = "corr-1" },
    }.ToJsonString());

    private AttemptRunner Runner()
    {
        var stager = new UpstreamStager(_client, new HttpClient(_storage), _time, _options, false, freeSpace: _freeSpace);
        return new AttemptRunner(_client, new SnapshottingAdapter(_runtime, _snapshot), _time, stager: stager);
    }

    private async Task<AttemptRunEnd> RunAsync(bool withUpstream = true)
    {
        var task = Runner().RunAsync(Delivery(withUpstream), () => _session, null, CancellationToken.None);
        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<IReadOnlyCollection<SimulatedAttempt>> WaitForAttemptsAsync(int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (_runtime.Attempts.Count != expected)
            {
                await Task.Delay(5, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller's assertion reports the observed count.
        }

        return _runtime.Attempts;
    }

    private int Calls(WorkOperation operation) => _platform.Headers.Count(h => h.Operation == operation);

    private JsonNode LastCompletion() =>
        JsonNode.Parse(_platform.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal)).Body)!;

    // The staged data is deleted when the attempt ends, so the content is copied at launch time.
    private readonly string _snapshot = Path.Combine(Path.GetTempPath(), "socalytics-stager-test-" + Guid.NewGuid().ToString("N"));

    private string InputsDirectory => Path.Combine(UpstreamStager.DirectoryFor(_attemptId), "inputs");

    private string StagedDirectory => Path.Combine(_snapshot, "inputs");

    private sealed class SnapshottingAdapter(SimulatedRuntimeAdapter inner, string snapshot) : IContainerRuntimeAdapter
    {
        public string Profile => inner.Profile;

        public IReadOnlyList<QueueCapability> Capabilities => inner.Capabilities;

        public Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken) =>
            inner.ProbeAsync(request, cancellationToken);

        public Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken)
        {
            if (launch.InputsDirectory is { } source)
            {
                CopyDirectory(source, Path.Combine(snapshot, "inputs"));
            }

            return inner.StartAttemptAsync(launch, cancellationToken);
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }
    }

    private void ExpectFailedCompletion(string category)
    {
        Calls(WorkOperation.Completion).ShouldBe(1);
        _runtime.Attempts.Count.ShouldBe(0);
        var completion = LastCompletion();
        ((string)completion["outcome"]!).ShouldBe("failed");
        ((string)completion["failure"]!["category"]!).ShouldBe(category);
        ShouldBeValidCompletion(completion);
        Directory.Exists(InputsDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Pages_are_staged_verified_and_the_analyst_launches_with_the_inputs_mount()
    {
        var first = AddResult("seg-1");
        var second = AddResult("seg-2");
        _pages.Enqueue(Page([first], "next-page", Accepted(first)));
        _pages.Enqueue(Page([second], null, Accepted(second)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.UpstreamRead).ShouldBe(2);
        (await WaitForAttemptsAsync(1)).Single().Launch.InputsDirectory.ShouldBe(InputsDirectory);
        foreach (var result in new[] { first, second })
        {
            var directory = Path.Combine(StagedDirectory, Upstream, result.Id.ToString());
            File.ReadAllBytes(Path.Combine(directory, "manifest.json")).ShouldBe(result.Manifest);
            File.ReadAllBytes(Path.Combine(directory, "facts.json")).ShouldBe(result.Facts);
        }

        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(StagedDirectory, Upstream, "coverage.json")))!;
        coverage["coverage"]!.AsArray().Select(c => (string)c!["segmentId"]!).ShouldBe(["seg-1", "seg-2"]);
        ((string)LastCompletion()["outcome"]!).ShouldBe("succeeded");
    }

    [Fact]
    public async Task Coverage_file_validates_against_the_contract_definitions_and_keeps_non_accepted_states()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null,
            Accepted(result) + """,{"segmentId":"seg-2","state":"missing"},{"segmentId":"seg-3","state":"failed"},{"segmentId":"seg-4","state":"blocked"},{"segmentId":"seg-5","state":"skipped"}"""));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(StagedDirectory, Upstream, "coverage.json")))!;
        coverage["coverage"]!.AsArray().Count.ShouldBe(5);
        foreach (var entry in coverage["coverage"]!.AsArray())
        {
            Validate("#/$defs/coverageEntry", entry).ShouldBeTrue();
        }

        Validate("#/properties/upstream", coverage["upstream"]).ShouldBeTrue();
        Validate("#/$defs/coverageEntry", JsonNode.Parse("""{"segmentId":"s","state":"unknown"}""")).ShouldBeFalse();
        Validate("#/properties/upstream", JsonNode.Parse("""{"capability":{"id":"c","version":"1"}}""")).ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_mode_is_staged_next_to_the_coverage()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null, Accepted(result), "1.1.0", """{"mode":"complete"}"""));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(StagedDirectory, Upstream, "coverage.json")))!;
        ((string)coverage["mode"]!).ShouldBe("complete");
        coverage["minimumAcceptedFraction"].ShouldBeNull();
        Validate("#/properties/upstream", coverage["upstream"]).ShouldBeTrue();
    }

    [Fact]
    public async Task Partial_mode_with_its_fraction_and_non_accepted_states_is_staged()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null, Accepted(result) + """,{"segmentId":"seg-2","state":"failed"}""", "1.1.0",
            """{"mode":"partial","minimumAcceptedFraction":0.5}"""));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(StagedDirectory, Upstream, "coverage.json")))!;
        ((string)coverage["mode"]!).ShouldBe("partial");
        ((double)coverage["minimumAcceptedFraction"]!).ShouldBe(0.5);
        coverage["coverage"]!.AsArray().Select(c => (string)c!["state"]!).ShouldBe(["accepted", "failed"]);
        Validate("#/properties/upstream", coverage["upstream"]).ShouldBeTrue();
    }

    [Fact]
    public async Task Version_1_0_0_pages_without_a_policy_are_staged_as_complete()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(StagedDirectory, Upstream, "coverage.json")))!;
        ((string)coverage["mode"]!).ShouldBe("complete");
    }

    [Fact]
    public async Task Retried_attempt_stages_an_identical_coverage_file()
    {
        var result = AddResult("seg-1");
        var page = Page([result], null, Accepted(result) + """,{"segmentId":"seg-2","state":"failed"}""", "1.1.0",
            """{"mode":"partial","minimumAcceptedFraction":0.5}""");
        _pages.Enqueue(page);
        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);
        var path = Path.Combine(StagedDirectory, Upstream, "coverage.json");
        var first = File.ReadAllBytes(path);

        Directory.Delete(_snapshot, true);
        _pages.Enqueue(page);
        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        File.ReadAllBytes(path).ShouldBe(first);
    }

    [Fact]
    public async Task Job_without_upstream_dependencies_has_no_inputs_and_no_read()
    {
        (await RunAsync(withUpstream: false)).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.UpstreamRead).ShouldBe(0);
        (await WaitForAttemptsAsync(1)).Single().Launch.InputsDirectory.ShouldBeNull();
        Directory.Exists(InputsDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Artifact_digest_mismatch_fails_the_attempt_as_input_unavailable_without_launching()
    {
        var result = AddResult("seg-1");
        _storage.Objects[$"/{result.Id}/facts.json"] = Encoding.UTF8.GetBytes("{\"tampered\":true}");
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("input-unavailable");
    }

    [Fact]
    public async Task Manifest_checksum_mismatch_fails_the_attempt_as_input_unavailable_without_launching()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null, Accepted(result)));
        _storage.Objects[$"/{result.Id}/manifest.json"] = Encoding.UTF8.GetBytes("""{"artifacts":[]}""");

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("input-unavailable");
    }

    [Fact]
    public async Task Summed_size_above_the_limit_is_refused_as_resource_exhausted_before_any_download()
    {
        var result = AddResult("seg-1", declaredSize: StagingOptions.MinimumBytes + 1);
        _options.MaxBytesPerAttempt = StagingOptions.MinimumBytes;
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("resource-exhausted");
        _storage.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Summed_size_above_the_free_space_is_refused_as_resource_exhausted_before_any_download()
    {
        var result = AddResult("seg-1", declaredSize: 4096);
        _freeSpace = _ => 1024;
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("resource-exhausted");
        _storage.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Expired_storage_grant_is_renewed_by_reading_again_while_the_attempt_is_current()
    {
        var result = AddResult("seg-1");
        _storage.Forbidden[$"/{result.Id}/facts.json"] = 1;
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.UpstreamRead).ShouldBe(2);
        File.ReadAllBytes(Path.Combine(StagedDirectory, Upstream, result.Id.ToString(), "facts.json")).ShouldBe(result.Facts);
        ((string)LastCompletion()["outcome"]!).ShouldBe("succeeded");
    }

    [Fact]
    public async Task Obsolete_read_stops_the_attempt_without_a_completion_or_launch()
    {
        _platform.Script(WorkOperation.UpstreamRead, () => Json(HttpStatusCode.Conflict,
            """{"type":"urn:socalytics:problem:attempt-obsolete","title":"t","status":409,"code":"attempt-obsolete","correlationId":"c"}"""));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Abandoned);

        Calls(WorkOperation.Completion).ShouldBe(0);
        _runtime.Attempts.Count.ShouldBe(0);
        _storage.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refused_read_fails_the_attempt_as_input_unavailable()
    {
        _platform.Script(WorkOperation.UpstreamRead, () => Json(HttpStatusCode.BadRequest,
            """{"type":"urn:socalytics:problem:upstream-dependency-unknown","title":"t","status":400,"code":"upstream-dependency-unknown","correlationId":"c"}"""));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("input-unavailable");
    }

    [Fact]
    public async Task Storage_unavailability_is_retried_with_backoff_and_then_succeeds()
    {
        var result = AddResult("seg-1");
        _storage.UnavailableResponses = 3;
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        _storage.Requests.Count.ShouldBeGreaterThan(3);
        (await WaitForAttemptsAsync(1)).Count.ShouldBe(1);
        ((string)LastCompletion()["outcome"]!).ShouldBe("succeeded");
    }

    [Fact]
    public async Task Storage_unavailable_until_the_execution_timeout_fails_the_attempt_as_input_unavailable()
    {
        var result = AddResult("seg-1");
        _storage.UnavailableResponses = int.MaxValue;
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        ExpectFailedCompletion("input-unavailable");
    }

    [Fact]
    public async Task Grant_urls_never_appear_in_the_launch()
    {
        var result = AddResult("seg-1");
        _pages.Enqueue(Page([result], null, Accepted(result)));

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var launch = (await WaitForAttemptsAsync(1)).Single().Launch;
        JsonSerializer.Serialize(launch with { Job = null }).ShouldNotContain("storage.test");
        JsonSerializer.Serialize(launch with { Job = null }).ShouldNotContain("sig=");
    }

    private static readonly Lazy<(SchemaRegistry Registry, BuildOptions Build)> Schemas = new(() =>
    {
        var registry = new SchemaRegistry { Fetch = (_, _) => null };
        var build = new BuildOptions { SchemaRegistry = registry };
        var root = Path.Combine(AppContext.BaseDirectory, "contracts");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        using var probe = JsonDocument.Parse("null");
        var loaded = Directory.EnumerateFiles(root, "*.schema.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}releases{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}examples{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (f.Contains("upstream-results", StringComparison.Ordinal) || f.Contains("attempt-completion", StringComparison.Ordinal)
                    || f.Contains("result-manifest", StringComparison.Ordinal) || f.Contains("common.schema", StringComparison.Ordinal)))
            .Select(f => JsonSchema.FromText(File.ReadAllText(f), build))
            .ToList();
        // Reference resolution is lazy and not thread-safe, so resolve it up front.
        foreach (var schema in loaded)
        {
            schema.Evaluate(probe.RootElement, options);
        }

        return (registry, build);
    });

    private static bool Validate(string pointer, JsonNode? instance)
    {
        var (_, build) = Schemas.Value;
        var wrapper = JsonSchema.FromText(
            $$"""{"$ref":"https://socalytics.invalid/contracts/analysis/upstream-results/v1/upstream-results.schema.json{{pointer}}"}""", build);
        using var document = JsonDocument.Parse(instance?.ToJsonString() ?? "null");
        return wrapper.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true }).IsValid;
    }

    private static void ShouldBeValidCompletion(JsonNode completion)
    {
        var (_, build) = Schemas.Value;
        var wrapper = JsonSchema.FromText(
            """{"$ref":"https://socalytics.invalid/contracts/analysis/attempt-completion/v1/attempt-completion.schema.json"}""", build);
        using var document = JsonDocument.Parse(completion.ToJsonString());
        var evaluation = wrapper.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        evaluation.IsValid.ShouldBeTrue(string.Join('\n', (evaluation.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Values)));
    }

    private sealed class FakeStorage : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Objects { get; } = [];

        public Dictionary<string, int> Forbidden { get; } = [];

        public int UnavailableResponses { get; set; }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (UnavailableResponses > 0)
            {
                UnavailableResponses--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (Forbidden.TryGetValue(path, out var remaining) && remaining > 0)
            {
                Forbidden[path] = remaining - 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(Objects.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
