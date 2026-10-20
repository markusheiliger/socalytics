#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

/// <summary>One attempt directory holds job.json, inputs, result, and scratch, is deleted on every end, and heartbeats cover a pending start (FR-011).</summary>
public sealed class AttemptDirectoryTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid OccurrenceId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly Guid _attemptId = Guid.NewGuid();
    private int _attemptNumber = 1;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly PlatformSession _session;
    private readonly PlatformClient _client;
    private readonly ProbingRuntime _runtime = new();

    public AttemptDirectoryTests()
    {
        _platform = new FakePlatform(_time);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","readinessOccurrenceId":"{{OccurrenceId}}","attemptNumber":{{_attemptNumber}},"fencingToken":7,"leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}"""));
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{_attemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));
        _platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private InMemoryDelivery Delivery() => new(_platform, MatchJob().ToJsonString());

    private static JsonObject MatchJob() => new()
    {
        ["contractVersion"] = "1.0.0",
        ["messageId"] = MessageId.ToString(),
        ["readinessOccurrenceId"] = OccurrenceId.ToString(),
        ["runId"] = "run-1",
        ["nodeId"] = "node-1",
        ["logicalJobId"] = JobId.ToString(),
        ["teamId"] = "team-1",
        ["matchId"] = "match-1",
        ["capability"] = new JsonObject { ["id"] = "player-detection", ["version"] = "1.0.0" },
        ["schemaVersion"] = "1.0.0",
        ["executionScope"] = "match",
        ["inputs"] = new JsonArray(),
        ["pinned"] = new JsonObject
        {
            ["analystProfileDigest"] = "sha-256:" + new string('3', 64),
            ["imageDigest"] = "sha-256:" + new string('4', 64),
            ["modelDigest"] = "sha-256:" + new string('5', 64),
        },
        ["resources"] = new JsonObject { ["cpuMillicores"] = 1000, ["memoryMebibytes"] = 2048 },
        ["correlation"] = new JsonObject { ["correlationId"] = "corr-1" },
    };

    private async Task<AttemptRunEnd> RunAsync(Func<bool>? until = null, InMemoryDelivery? delivery = null)
    {
        var publisher = new ResultPublisher(_client, new HttpClient(), _time, false);
        var runner = new AttemptRunner(_client, _runtime, _time, publisher: publisher);
        var task = runner.RunAsync(delivery ?? Delivery(), () => _session, null, CancellationToken.None);
        for (var i = 0; i < 3000 && !task.IsCompleted && !(until?.Invoke() ?? false); i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        if (until is not null)
        {
            _runtime.Release();
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private int Heartbeats => _platform.Headers.Count(h => h.Operation == WorkOperation.Heartbeat);

    [Fact]
    public async Task The_attempt_directory_holds_the_job_file_and_the_result_and_scratch_directories()
    {
        _runtime.Outcome = AttemptOutcome.Failure("analyst lost");

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var launch = _runtime.Launch.ShouldNotBeNull();
        var directory = UpstreamStager.DirectoryFor(_attemptId);
        launch.JobFile.ShouldBe(Path.Combine(directory, "job.json"));
        launch.ResultDirectory.ShouldBe(Path.Combine(directory, "result"));
        launch.ScratchDirectory.ShouldBe(Path.Combine(directory, "scratch"));
        launch.InputsDirectory.ShouldBeNull();
        _runtime.Observed.ShouldBe(["attempt.json", "job.json", "result", "scratch"]);

        var job = JsonNode.Parse(_runtime.JobJson!)!;
        job["logicalJobId"]!.GetValue<string>().ShouldBe(JobId.ToString());
        job["capability"]!["id"]!.GetValue<string>().ShouldBe("player-detection");
        job["pinned"]!["imageDigest"]!.GetValue<string>().ShouldBe("sha-256:" + new string('4', 64));
        ResultPublisher.DirectoryFor(_attemptId).ShouldBe(launch.ResultDirectory);
        launch.AttemptFile.ShouldBe(Path.Combine(directory, "attempt.json"));
    }

    [Fact]
    public async Task A_job_message_of_contract_version_1_0_0_is_written_byte_identical_and_still_runs_with_the_attempt_context()
    {
        var payload = MatchJob().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        _runtime.Outcome = AttemptOutcome.Failure("analyst lost");

        (await RunAsync(delivery: new InMemoryDelivery(_platform, payload))).ShouldBe(AttemptRunEnd.Completed);

        _runtime.JobBytes.ShouldBe(System.Text.Encoding.UTF8.GetBytes(payload));
        var attempt = JsonNode.Parse(_runtime.AttemptJson!)!.AsObject();
        attempt["contractVersion"]!.GetValue<string>().ShouldBe("1.0.0");
        attempt["attemptId"]!.GetValue<string>().ShouldBe(_attemptId.ToString());
        attempt["attemptNumber"]!.GetValue<int>().ShouldBe(1);
        IsValid("analyst-attempt", System.Text.Encoding.UTF8.GetBytes(_runtime.AttemptJson!)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_job_file_is_byte_identical_to_a_1_1_0_delivery_and_valid_against_the_job_contract()
    {
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "analysis", "analyst-job", "v1", "examples", "valid", "segment-job-with-window.json")))!.AsObject();
        example["messageId"] = MessageId.ToString();
        example["readinessOccurrenceId"] = OccurrenceId.ToString();
        example["logicalJobId"] = JobId.ToString();
        // Indented with a trailing newline, so a re-serialization would differ from the delivered bytes.
        var payload = example.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        _runtime.Outcome = AttemptOutcome.Failure("analyst lost");

        (await RunAsync(delivery: new InMemoryDelivery(_platform, payload))).ShouldBe(AttemptRunEnd.Completed);

        var written = _runtime.JobBytes.ShouldNotBeNull();
        written.ShouldBe(System.Text.Encoding.UTF8.GetBytes(payload));
        IsValid("analyst-job", written).ShouldBeTrue();
    }

    [Fact]
    public async Task The_attempt_file_names_the_new_attempt_on_a_retry_and_carries_no_lease_or_fencing_data()
    {
        _attemptNumber = 2;
        _runtime.Outcome = AttemptOutcome.Failure("analyst lost");

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        var attempt = JsonNode.Parse(_runtime.AttemptJson!)!.AsObject();
        attempt["attemptId"]!.GetValue<string>().ShouldBe(_attemptId.ToString());
        attempt["attemptNumber"]!.GetValue<int>().ShouldBe(2);
        attempt.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(["attemptId", "attemptNumber", "contractVersion"]);
        IsValid("analyst-attempt", System.Text.Encoding.UTF8.GetBytes(_runtime.AttemptJson!)).ShouldBeTrue();
    }

    private static readonly Lazy<BuildOptions> Schemas = new(() =>
    {
        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null } };
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        using var probe = JsonDocument.Parse("null");
        var loaded = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "contracts"), "*.schema.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}releases{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}examples{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (f.Contains("analyst-job", StringComparison.Ordinal) || f.Contains("analyst-attempt", StringComparison.Ordinal)
                    || f.Contains("common.schema", StringComparison.Ordinal)))
            .Select(f => JsonSchema.FromText(File.ReadAllText(f), build))
            .ToList();
        // Reference resolution is lazy and not thread-safe, so resolve it up front.
        foreach (var schema in loaded)
        {
            schema.Evaluate(probe.RootElement, options);
        }

        return build;
    });

    private static bool IsValid(string contract, byte[] json)
    {
        var wrapper = JsonSchema.FromText(
            $$"""{"$ref":"https://socalytics.invalid/contracts/analysis/{{contract}}/v1/{{contract}}.schema.json"}""", Schemas.Value);
        using var document = JsonDocument.Parse(json);
        return wrapper.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true }).IsValid;
    }

    [Theory]
    [InlineData("succeed")]
    [InlineData("fail")]
    [InlineData("throw")]
    public async Task The_whole_attempt_directory_is_deleted_on_every_end(string mode)
    {
        switch (mode)
        {
            case "succeed":
                _runtime.Outcome = AttemptOutcome.Success("ref", "sha-256:" + new string('a', 64));
                break;
            case "fail":
                _runtime.Outcome = AttemptOutcome.Failure("analyst lost");
                break;
            default:
                _runtime.Throw = true;
                break;
        }

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        Directory.Exists(UpstreamStager.DirectoryFor(_attemptId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Heartbeats_are_sent_while_the_start_of_the_attempt_is_pending()
    {
        _runtime.Block = true;
        var before = Heartbeats;

        (await RunAsync(() => Heartbeats > before + 1)).ShouldBe(AttemptRunEnd.Completed);

        Heartbeats.ShouldBeGreaterThan(before + 1);
        _runtime.Launch.ShouldNotBeNull();
        Directory.Exists(UpstreamStager.DirectoryFor(_attemptId)).ShouldBeFalse();
    }

    private sealed class ProbingRuntime : IContainerRuntimeAdapter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Profile => "simulated";

        public AttemptLaunch? Launch { get; private set; }

        public string? JobJson { get; private set; }

        public byte[]? JobBytes { get; private set; }

        public string? AttemptJson { get; private set; }

        public List<string> Observed { get; private set; } = [];

        public AttemptOutcome Outcome { get; set; } = AttemptOutcome.Failure("analyst lost");

        public bool Block { get; set; }

        public bool Throw { get; set; }

        public void Release() => _release.TrySetResult();

        public Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken)
        {
            Launch = launch;
            var directory = Path.GetDirectoryName(launch.JobFile)!;
            Observed = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()!;
            JobBytes = await File.ReadAllBytesAsync(launch.JobFile!, cancellationToken);
            JobJson = System.Text.Encoding.UTF8.GetString(JobBytes);
            AttemptJson = await File.ReadAllTextAsync(launch.AttemptFile!, cancellationToken);
            if (Throw)
            {
                throw new InvalidOperationException("the runtime refused the attempt");
            }

            if (Block)
            {
                await _release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("released");
            }

            return new Execution(Outcome);
        }
    }

    private sealed class Execution(AttemptOutcome outcome) : IAttemptExecution
    {
        public Task<AttemptOutcome> Outcome { get; } = Task.FromResult(outcome);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CleanUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
