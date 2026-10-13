#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public class AttemptRunnerTests
{
    private const string Capability = "player-detection";
    private const long Token = 424242;
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MessageId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string ImageDigest = "sha-256:" + new string('4', 64);
    private static readonly string ModelDigest = "sha-256:" + new string('5', 64);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly FakeLogCollector _logs = new();
    private readonly SimulatedAnalystOptions _simulated = new();
    private readonly SimulatedRuntimeAdapter _runtime;
    private readonly AttemptRunner _runner;
    private readonly PlatformSession _session;
    private int _leaseSeconds = 60;
    private int _executionTimeoutSeconds = 600;

    public AttemptRunnerTests()
    {
        _platform = new FakePlatform(_time);
        var client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _runtime = new SimulatedRuntimeAdapter(_simulated, _time);
        var factory = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        _runner = new AttemptRunner(client, _runtime, _time, factory.CreateLogger<AttemptRunner>());
        _platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created, Granted(false)));
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(_leaseSeconds):O}}"}"""));
        _platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created, Completed(false)));
    }

    private string Granted(bool replayed) =>
        $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","readinessOccurrenceId":"{{OccurrenceId}}","attemptNumber":1,"fencingToken":{{Token}},"leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(_leaseSeconds):O}}","leaseSeconds":{{_leaseSeconds}},"staleTimeoutSeconds":120,"executionTimeoutSeconds":{{_executionTimeoutSeconds}},"replayed":{{(replayed ? "true" : "false")}}}""";

    private static string Completed(bool replayed) =>
        $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":{{(replayed ? "true" : "false")}},"logicalJobState":"succeeded"}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => Json(status,
        $$"""{"type":"urn:socalytics:problem:{{code}}","title":"t","status":{{(int)status}},"code":"{{code}}","correlationId":"c"}""");

    private static InMemoryDelivery Delivery(FakePlatform platform) => new(platform, new JsonObject
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
        ["inputs"] = new JsonArray(new JsonObject
        {
            ["acceptedResults"] = new JsonArray(new JsonObject { ["acceptedResultId"] = "res-1" }),
        }),
        ["pinned"] = new JsonObject
        {
            ["analystProfileDigest"] = "sha-256:" + new string('3', 64),
            ["imageDigest"] = ImageDigest,
            ["modelDigest"] = ModelDigest,
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

    private Task<AttemptRunEnd> Run(InMemoryDelivery delivery, Action<ActiveAttempt>? started = null) =>
        _runner.RunAsync(delivery, () => _session, started, CancellationToken.None);

    private IEnumerable<string> Messages => _logs.GetSnapshot().Select(r => r.Message);

    private int Calls(WorkOperation operation) => _platform.Headers.Count(h => h.Operation == operation);

    [Fact]
    public async Task Successful_attempt_claims_acknowledges_completes_and_cleans_up()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { RunDuration = TimeSpan.FromSeconds(5) };
        var delivery = Delivery(_platform);
        ActiveAttempt? seen = null;

        var end = await Drive(Run(delivery, a => seen = a));

        end.ShouldBe(AttemptRunEnd.Completed);
        delivery.Acks.ShouldBe(1);
        delivery.Naks.ShouldBe(0);
        Calls(WorkOperation.Claim).ShouldBe(1);
        Calls(WorkOperation.Completion).ShouldBe(1);
        _platform.Headers.Single(h => h.Operation == WorkOperation.Claim).IdempotencyKey.ShouldBe($"{MessageId}:{OccurrenceId}");
        seen!.AttemptId.ShouldBe(AttemptId);

        var simulated = _runtime.Attempts.Single();
        simulated.Launch.ImageDigest.ShouldBe(ImageDigest);
        simulated.Launch.ModelDigest.ShouldBe(ModelDigest);
        simulated.CleanedUp.ShouldBeTrue();

        var body = JsonNode.Parse(_platform.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal)).Body)!;
        ((string)body["outcome"]!).ShouldBe("succeeded");
        ((string)body["idempotencyKey"]!).ShouldBe($"completion:{AttemptId}");
        ((long)body["fencingToken"]!).ShouldBe(Token);
        ((string)body["executedImageDigest"]!).ShouldBe(ImageDigest);
        ((string)body["executedModelDigest"]!).ShouldBe(ModelDigest);
        ((string)body["acceptedUpstreamResultIds"]![0]!).ShouldBe("res-1");
    }

    [Fact]
    public async Task Acknowledges_only_after_the_claim_outcome()
    {
        var delivery = Delivery(_platform);
        _platform.Script(WorkOperation.Claim, () => throw new HttpRequestException("unreachable"));

        var end = await Drive(Run(delivery));

        end.ShouldBe(AttemptRunEnd.ClaimDeferred);
        delivery.Acks.ShouldBe(0);
        Calls(WorkOperation.Claim).ShouldBeGreaterThan(1);
        _platform.Headers.Where(h => h.Operation == WorkOperation.Claim).Select(h => h.IdempotencyKey).Distinct().Count().ShouldBe(1);
        _runtime.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refused_claim_is_not_acknowledged()
    {
        var delivery = Delivery(_platform);
        _platform.Script(WorkOperation.Claim, () => Problem(HttpStatusCode.UnprocessableEntity, "capability-not-offered"));

        (await Drive(Run(delivery))).ShouldBe(AttemptRunEnd.Rejected);

        delivery.Acks.ShouldBe(0);
        _runtime.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Obsolete_claim_is_acknowledged_without_work()
    {
        var delivery = Delivery(_platform);
        _platform.Script(WorkOperation.Claim, () => Problem(HttpStatusCode.Conflict, "claim-obsolete"));

        (await Drive(Run(delivery))).ShouldBe(AttemptRunEnd.ClaimObsolete);

        delivery.Acks.ShouldBe(1);
        delivery.AcksAfterClaims.ShouldBe([1]);
        _runtime.Attempts.ShouldBeEmpty();
        Calls(WorkOperation.Heartbeat).ShouldBe(0);
        Calls(WorkOperation.Completion).ShouldBe(0);
    }

    [Fact]
    public async Task Lost_claim_and_completion_responses_are_replayed_with_the_same_keys()
    {
        var claims = 0;
        _platform.Script(WorkOperation.Claim, () => ++claims == 1
            ? throw new HttpRequestException("lost")
            : Json(HttpStatusCode.Created, Granted(false)));
        var completions = 0;
        _platform.Script(WorkOperation.Completion, () => ++completions < 3
            ? throw new HttpRequestException("lost")
            : Json(HttpStatusCode.OK, Completed(true)));
        var delivery = Delivery(_platform);

        (await Drive(Run(delivery))).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.Claim).ShouldBe(2);
        _platform.Headers.Where(h => h.Operation == WorkOperation.Claim).Select(h => h.IdempotencyKey).Distinct()
            .ShouldBe([$"{MessageId}:{OccurrenceId}"]);
        Calls(WorkOperation.Completion).ShouldBe(3);
        delivery.Acks.ShouldBe(1);
        var bodies = _platform.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal))
            .Select(r => JsonNode.Parse(r.Body)!).ToList();
        bodies.Count.ShouldBe(3);
        bodies.Select(b => (string)b["idempotencyKey"]!).Distinct().ShouldBe([$"completion:{AttemptId}"]);
        bodies.Select(b => (string)b["outcome"]!).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Heartbeats_at_a_third_of_the_lease_keep_it_alive_and_record_the_last_confirmation()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { RunDuration = TimeSpan.FromSeconds(70) };
        var states = new List<string>();
        _platform.Script(WorkOperation.Heartbeat, () =>
        {
            states.Add(LastState());
            return Json(HttpStatusCode.OK, $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}""");
        });
        ActiveAttempt? seen = null;

        (await Drive(Run(Delivery(_platform), a => seen = a))).ShouldBe(AttemptRunEnd.Completed);

        Calls(WorkOperation.Heartbeat).ShouldBe(3);
        states.ShouldAllBe(s => s == "running");
        seen!.LastConfirmedHeartbeat.ShouldNotBeNull();
        seen.LastConfirmedHeartbeat!.Value.ShouldBeGreaterThan(seen.StartedAt.AddSeconds(55));
        var observed = _platform.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/heartbeats", StringComparison.Ordinal))
            .Select(r => DateTimeOffset.Parse((string)JsonNode.Parse(r.Body)!["observedAt"]!)).ToList();
        for (var i = 1; i < observed.Count; i++)
        {
            (observed[i] - observed[i - 1]).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(21));
        }
    }

    private string LastState() =>
        (string)JsonNode.Parse(_platform.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/heartbeats", StringComparison.Ordinal)).Body)!["executionState"]!;

    [Fact]
    public async Task Transient_heartbeat_failure_is_logged_and_retried_before_the_lease_expires()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { RunDuration = TimeSpan.FromSeconds(50) };
        var beats = 0;
        _platform.Script(WorkOperation.Heartbeat, () => ++beats == 1
            ? throw new HttpRequestException("lost")
            : Json(HttpStatusCode.OK, $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));

        (await Drive(Run(Delivery(_platform)))).ShouldBe(AttemptRunEnd.Completed);

        beats.ShouldBeGreaterThan(1);
        Messages.ShouldContain(m => m.StartsWith("Heartbeat failed", StringComparison.Ordinal) && m.Contains(JobId.ToString()) && m.Contains(AttemptId.ToString()) && m.Contains("corr-1"));
    }

    [Fact]
    public async Task Fenced_heartbeat_abandons_the_attempt_without_a_completion()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _platform.Script(WorkOperation.Heartbeat, () => Problem(HttpStatusCode.Conflict, "attempt-obsolete"));

        (await Drive(Run(Delivery(_platform)))).ShouldBe(AttemptRunEnd.Abandoned);

        Calls(WorkOperation.Completion).ShouldBe(0);
        _runtime.Attempts.Single().CleanedUp.ShouldBeTrue();
        Messages.ShouldContain(m => m.Contains("was fenced") && m.Contains(AttemptId.ToString()) && m.Contains(JobId.ToString()) && m.Contains("corr-1"));
    }

    [Fact]
    public async Task Obsolete_completion_abandons_the_attempt()
    {
        _platform.Script(WorkOperation.Completion, () => Problem(HttpStatusCode.Conflict, "attempt-obsolete"));

        (await Drive(Run(Delivery(_platform)))).ShouldBe(AttemptRunEnd.Abandoned);

        Calls(WorkOperation.Completion).ShouldBe(1);
        Messages.ShouldContain(m => m.Contains("completion rejected as obsolete"));
    }

    [Fact]
    public async Task Failed_analyst_reports_a_failed_completion_and_hang_times_out()
    {
        _simulated.Capabilities[Capability] = new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang };
        _executionTimeoutSeconds = 30;
        _platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"{{_time.GetUtcNow().AddSeconds(60):O}}"}"""));

        (await Drive(Run(Delivery(_platform)))).ShouldBe(AttemptRunEnd.Completed);

        var body = JsonNode.Parse(_platform.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal)).Body)!;
        ((string)body["outcome"]!).ShouldBe("failed");
        ((string)body["failure"]!["category"]!).ShouldBe("execution-timeout");
        var simulated = _runtime.Attempts.Single();
        simulated.Stopped.ShouldBeTrue();
        simulated.CleanedUp.ShouldBeTrue();
    }

    [Fact]
    public async Task Logs_claims_acknowledgements_completions_and_clean_up_with_identities_but_no_secrets()
    {
        await Drive(Run(Delivery(_platform)));

        var messages = Messages.ToList();
        foreach (var prefix in new[] { "Claimed", "Acknowledged", "Completed", "Cleaned up" })
        {
            messages.ShouldContain(m => m.StartsWith(prefix, StringComparison.Ordinal)
                && m.Contains(JobId.ToString()) && m.Contains("corr-1")
                && (prefix == "Acknowledged" || m.Contains(AttemptId.ToString())));
        }

        string.Join('\n', _logs.GetSnapshot().Select(r => r.Message + " " + string.Join(' ', r.StructuredState?.Select(p => p.Value) ?? [])))
            .ShouldNotContain(Token.ToString());
    }
}

internal sealed class InMemoryDelivery(FakePlatform platform, string payload) : IQueueDelivery
{
    public string Capability => "player-detection";

    public ReadOnlyMemory<byte> Data { get; } = System.Text.Encoding.UTF8.GetBytes(payload);

    public int Acks { get; private set; }

    public int Naks { get; private set; }

    /// <summary>Claim calls the platform had seen when each acknowledgement happened.</summary>
    public List<int> AcksAfterClaims { get; } = [];

    public Task AckAsync(CancellationToken cancellationToken)
    {
        Acks++;
        AcksAfterClaims.Add(platform.Headers.Count(h => h.Operation == WorkOperation.Claim));
        return Task.CompletedTask;
    }

    public Task NakAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Naks++;
        return Task.CompletedTask;
    }

    public Task NakAsync(CancellationToken cancellationToken)
    {
        Naks++;
        return Task.CompletedTask;
    }
}
