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

/// <summary>User Story 3 at the Manager level: failures are reported once and fenced attempts are abandoned silently.</summary>
public class AttemptFailureTests
{
    private const string Capability = "player-detection";
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MessageId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly SimulatedAnalystOptions _simulated = new();
    private readonly SimulatedRuntimeAdapter _runtime;
    private readonly PlatformClient _client;
    private readonly AttemptRunner _runner;
    private readonly PlatformSession _session;
    private int _executionTimeoutSeconds = 600;

    public AttemptFailureTests()
    {
        _platform = new FakePlatform(_time);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _runtime = new SimulatedRuntimeAdapter(_simulated, _time);
        _runner = new AttemptRunner(_client, _runtime, _time);
        _platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","readinessOccurrenceId":"{{OccurrenceId}}","attemptNumber":1,"fencingToken":7,"leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":{{_executionTimeoutSeconds}},"replayed":false}"""));
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));
        _platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Obsolete() => Json(HttpStatusCode.Conflict,
        """{"type":"urn:socalytics:problem:attempt-obsolete","title":"t","status":409,"code":"attempt-obsolete","correlationId":"c"}""");

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

    private async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private Task<AttemptRunEnd> Run(Action<ActiveAttempt>? started = null) =>
        _runner.RunAsync(Delivery(), () => _session, started, CancellationToken.None);

    private int Calls(WorkOperation operation) => _platform.Headers.Count(h => h.Operation == operation);

    private JsonNode LastCompletion() =>
        JsonNode.Parse(_platform.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal)).Body)!;

    /// <summary>Makes every completion record whether the simulated work was already stopped and cleaned up.</summary>
    private List<(bool Stopped, bool CleanedUp)> RecordStateAtCompletion()
    {
        var states = new List<(bool, bool)>();
        _platform.Script(WorkOperation.Completion, () =>
        {
            var attempt = _runtime.Attempts.Single();
            states.Add((attempt.Stopped, attempt.CleanedUp));
            return Json(HttpStatusCode.Created,
                $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"ready"}""");
        });
        return states;
    }

    [Fact]
    public async Task Failed_analyst_is_cleaned_up_and_reported_once_without_a_retry()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Fail, FailureReason = "boom" };
        var states = RecordStateAtCompletion();

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.Completion).ShouldBe(1);
        Calls(WorkOperation.Claim).ShouldBe(1);
        _runtime.Attempts.Count.ShouldBe(1);
        ((string)LastCompletion()["outcome"]!).ShouldBe("failed");
        states.ShouldBe([(true, true)]);
    }

    [Fact]
    public async Task Crashed_analyst_is_cleaned_up_and_reported_failed_once()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Crash };
        var states = RecordStateAtCompletion();

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.Completion).ShouldBe(1);
        ((string)LastCompletion()["outcome"]!).ShouldBe("failed");
        states.ShouldBe([(true, true)]);
        _runtime.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Execution_timeout_stops_cleans_up_and_reports_the_timeout()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _executionTimeoutSeconds = 90;
        var states = RecordStateAtCompletion();

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Completed);

        ((string)LastCompletion()["failure"]!["category"]!).ShouldBe("execution-timeout");
        Calls(WorkOperation.Completion).ShouldBe(1);
        states.ShouldBe([(true, true)]);
        Calls(WorkOperation.Heartbeat).ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task Failed_completion_replays_lost_responses_with_the_same_key_and_still_reports_once()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Fail };
        var completions = 0;
        _platform.Script(WorkOperation.Completion, () => ++completions < 3
            ? throw new HttpRequestException("lost")
            : Json(HttpStatusCode.OK,
                $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":true,"logicalJobState":"ready"}"""));

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Completed);

        _platform.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal))
            .Select(r => (string)JsonNode.Parse(r.Body)!["idempotencyKey"]!).Distinct().ShouldBe([$"completion.{AttemptId}"]);
        Calls(WorkOperation.Completion).ShouldBe(3);
        _runtime.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unreachable_heartbeats_retry_with_backoff_before_the_lease_expires_and_never_assume_renewal()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        var leaseExpiry = _time.GetUtcNow();
        var beats = 0;
        var tries = new List<DateTimeOffset>();
        _platform.Script(WorkOperation.Heartbeat, () =>
        {
            tries.Add(_time.GetUtcNow());
            if (++beats > 1)
            {
                throw new HttpRequestException("unreachable");
            }

            leaseExpiry = _time.GetUtcNow().AddSeconds(60);
            return Json(HttpStatusCode.OK, $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{leaseExpiry:O}}"}""");
        });

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Abandoned);

        Calls(WorkOperation.Completion).ShouldBe(0);
        tries.Count.ShouldBeGreaterThan(3);
        tries.Skip(1).Count(t => t < leaseExpiry).ShouldBeGreaterThan(2);
        for (var i = 2; i < tries.Count; i++)
        {
            (tries[i] - tries[i - 1]).ShouldBeLessThan(TimeSpan.FromSeconds(30));
        }

        _runtime.Attempts.Single().CleanedUp.ShouldBeTrue();
    }

    [Fact]
    public async Task Fenced_heartbeat_stops_cleans_up_sends_nothing_more_and_frees_the_slot()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _platform.Script(WorkOperation.Heartbeat, Obsolete);
        var registry = new AttemptRegistry();

        var task = registry.Run((started, token) => _runner.RunAsync(Delivery(), () => _session, started, token), CancellationToken.None);
        await Drive(task.ContinueWith(_ => true, TaskScheduler.Default));

        registry.ActiveCount.ShouldBe(0);
        Calls(WorkOperation.Heartbeat).ShouldBe(1);
        Calls(WorkOperation.Completion).ShouldBe(0);
        var attempt = _runtime.Attempts.Single();
        attempt.Stopped.ShouldBeTrue();
        attempt.CleanedUp.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(20);
        Calls(WorkOperation.Heartbeat).ShouldBe(1);
        Calls(WorkOperation.Completion).ShouldBe(0);
    }

    [Fact]
    public async Task Obsolete_completion_is_not_retried_and_the_work_is_cleaned_up()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Fail };
        _platform.Script(WorkOperation.Completion, Obsolete);

        (await Drive(Run())).ShouldBe(AttemptRunEnd.Abandoned);

        Calls(WorkOperation.Completion).ShouldBe(1);
        _runtime.Attempts.Single().CleanedUp.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(20);
        Calls(WorkOperation.Completion).ShouldBe(1);
        Calls(WorkOperation.Heartbeat).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_runner_does_not_resume_attempts_of_a_previous_process()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        using var cts = new CancellationTokenSource();
        var first = _runner.RunAsync(Delivery(), () => _session, null, cts.Token);
        await Task.Delay(20);
        _time.Advance(TimeSpan.FromSeconds(1));
        await cts.CancelAsync();
        await Drive(first);

        var claimsBefore = Calls(WorkOperation.Claim);
        var heartbeatsBefore = Calls(WorkOperation.Heartbeat);
        var completionsBefore = Calls(WorkOperation.Completion);
        var registry = new AttemptRegistry();
        registry.ActiveCount.ShouldBe(0);
        _time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(20);

        Calls(WorkOperation.Claim).ShouldBe(claimsBefore);
        Calls(WorkOperation.Heartbeat).ShouldBe(heartbeatsBefore);
        Calls(WorkOperation.Completion).ShouldBe(completionsBefore);
    }
}
