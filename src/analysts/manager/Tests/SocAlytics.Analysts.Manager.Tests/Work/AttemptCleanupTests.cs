#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

/// <summary>Staged data never outlives its attempt: nothing remains after any kind of end or after a crash and restart (FR-014, SC-007).</summary>
public sealed class AttemptCleanupTests : IDisposable
{
    private const string Capability = "player-detection";
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid OccurrenceId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly Guid _attemptId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly SimulatedAnalystOptions _simulated = new();
    private readonly SimulatedRuntimeAdapter _runtime;
    private readonly PlatformSession _session;
    private readonly PlatformClient _client;
    private readonly List<string> _planted = [];
    private int _executionTimeoutSeconds = 600;

    public AttemptCleanupTests()
    {
        _platform = new FakePlatform(_time);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _runtime = new SimulatedRuntimeAdapter(_simulated, _time);
        _platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","readinessOccurrenceId":"{{OccurrenceId}}","attemptNumber":1,"fencingToken":7,"leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":{{_executionTimeoutSeconds}},"replayed":false}"""));
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{_attemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));
        _platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{_attemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
    }

    public void Dispose()
    {
        foreach (var directory in _planted)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private string PlantStagedData(Guid attemptId)
    {
        var directory = UpstreamStager.DirectoryFor(attemptId);
        var inputs = Path.Combine(directory, UpstreamStager.InputsFolder, "upstream", "result-1");
        Directory.CreateDirectory(inputs);
        File.WriteAllBytes(Path.Combine(inputs, "facts.json"), new byte[1024]);
        _planted.Add(directory);
        return directory;
    }

    private InMemoryDelivery Delivery() => new(_platform, new JsonObject
    {
        ["contractVersion"] = "1.0.0",
        ["messageId"] = MessageId.ToString(),
        ["readinessOccurrenceId"] = OccurrenceId.ToString(),
        ["runId"] = "run-1",
        ["nodeId"] = "node-1",
        ["logicalJobId"] = JobId.ToString(),
        ["teamId"] = "team-1",
        ["matchId"] = "match-1",
        ["capability"] = new JsonObject { ["id"] = Capability, ["version"] = "1.0.0" },
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
    }.ToJsonString());

    private async Task<AttemptRunEnd> RunAsync()
    {
        var stager = new UpstreamStager(_client, new HttpClient(), _time, new StagingOptions(), false);
        var runner = new AttemptRunner(_client, _runtime, _time, stager: stager);
        var task = runner.RunAsync(Delivery(), () => _session, null, CancellationToken.None);
        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(SimulatedOutcome.Succeed, AttemptRunEnd.Completed)]
    [InlineData(SimulatedOutcome.Fail, AttemptRunEnd.Completed)]
    [InlineData(SimulatedOutcome.Crash, AttemptRunEnd.Completed)]
    public async Task Nothing_remains_after_a_completed_or_failed_attempt(SimulatedOutcome outcome, AttemptRunEnd expected)
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = outcome };
        var directory = PlantStagedData(_attemptId);

        (await RunAsync()).ShouldBe(expected);

        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task Nothing_remains_after_a_timed_out_attempt()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _executionTimeoutSeconds = 30;
        var directory = PlantStagedData(_attemptId);

        (await RunAsync()).ShouldBe(AttemptRunEnd.Completed);

        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task Nothing_remains_after_a_fenced_attempt()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.Conflict,
            """{"type":"urn:socalytics:problem:attempt-obsolete","title":"t","status":409,"code":"attempt-obsolete","correlationId":"c"}"""));
        var directory = PlantStagedData(_attemptId);

        (await RunAsync()).ShouldBe(AttemptRunEnd.Abandoned);

        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void Attempt_directories_left_by_a_crashed_process_are_deleted_at_start()
    {
        var left = new[] { PlantStagedData(Guid.NewGuid()), PlantStagedData(Guid.NewGuid()) };

        AttemptRunner.DeleteLeftoverAttemptDirectories();

        left.ShouldAllBe(d => !Directory.Exists(d));
    }
}
