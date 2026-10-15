#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public sealed class WorkAcquisitionTests
{
    private const string Jwt = "eyJ0eXAiOiJKV1QifQ.eyJzdWIiOiJ1c2VyIn0.c2lnbmF0dXJlLXZhbHVl";
    private const string Detection = "player-detection";
    private const string Foreign = "ball-tracking";

    internal sealed class FakeQueue : IQueueConnection
    {
        public List<string> Fetches { get; } = [];

        public Queue<IQueueDelivery> Ready { get; } = new();

        public Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken)
        {
            Fetches.Add(capability);
            return Task.FromResult(Ready.TryDequeue(out var delivery) ? delivery : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static string Payload(int index, int cpuMillicores = 1000, long memoryMebibytes = 2048) => new JsonObject
    {
        ["contractVersion"] = "1.0.0",
        ["messageId"] = Guid.NewGuid().ToString(),
        ["readinessOccurrenceId"] = Guid.NewGuid().ToString(),
        ["runId"] = "run-1",
        ["nodeId"] = "node-" + index,
        ["logicalJobId"] = Guid.NewGuid().ToString(),
        ["teamId"] = "team-1",
        ["matchId"] = "match-1",
        ["recordingSetVersionId"] = "rsv-1",
        ["capability"] = new JsonObject { ["id"] = Detection, ["version"] = "1.0.0" },
        ["schemaVersion"] = "1.0.0",
        ["executionScope"] = "match",
        ["inputs"] = new JsonArray(),
        ["pinned"] = new JsonObject
        {
            ["analystProfileId"] = "profile-1",
            ["analystProfileDigest"] = "sha-256:" + new string('3', 64),
            ["imageDigest"] = "sha-256:" + new string('4', 64),
            ["modelDigest"] = "sha-256:" + new string('5', 64),
        },
        ["resources"] = new JsonObject { ["cpuMillicores"] = cpuMillicores, ["memoryMebibytes"] = memoryMebibytes },
        ["correlation"] = new JsonObject { ["correlationId"] = "corr-" + index },
    }.ToJsonString();

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static (ManagerTestHost Host, FakeQueue Queue) Host(SimulatedOutcome outcome, int runtimeMax, int stampMax)
    {
        var queue = new FakeQueue();
        var adapter = new SimulatedRuntimeAdapter(
            new SimulatedAnalystOptions
            {
                MaxParallelAttempts = runtimeMax,
                Capabilities = { [Detection] = new SimulatedCapabilityOptions { Outcome = outcome } },
            },
            TimeProvider.System);
        var host = new ManagerTestHost(configure: s =>
        {
            s.RemoveAll<IQueueConnection>();
            s.AddSingleton<IQueueConnection>(queue);
            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog([adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(SimulatedRuntimeAdapter.List());
            s.Configure<RuntimeOptions>(o => o.Profile = SimulatedRuntimeAdapter.TestProfile);
        });
        var platform = host.Platform;
        platform.Script(WorkOperation.QueueCredentials, () => Json(HttpStatusCode.Created,
            $$"""{"userJwt":"{{Jwt}}","expiresAt":"{{host.Time.GetUtcNow().AddHours(1):O}}","queueUrl":"tls://queue.test:4222","stream":"SOCALYTICS_ANALYSIS_JOBS","consumers":[{"capability":"{{Detection}}","consumer":"analyst-{{Detection}}"},{"capability":"{{Foreign}}","consumer":"analyst-{{Foreign}}"}],"inboxPrefix":"_INBOX_abc","maxConcurrentAnalysts":{{stampMax}}}"""));
        platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{Guid.NewGuid()}}","logicalJobId":"{{Guid.NewGuid()}}","readinessOccurrenceId":"{{Guid.NewGuid()}}","attemptNumber":1,"fencingToken":918273645,"leaseExpiresAt":"{{host.Time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}"""));
        platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{Guid.NewGuid()}}","leaseExpiresAt":"{{host.Time.GetUtcNow().AddSeconds(60):O}}"}"""));
        platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{Guid.NewGuid()}}","logicalJobId":"{{Guid.NewGuid()}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
        return (host, queue);
    }

    internal static async Task Restore(ManagerTestHost host, bool paused = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await host.Keys.CreateKeyAsync(false, ct);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                paused ? OperatingIntent.Paused : OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(ct);
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(ct);
    }

    internal static async Task<WorkAcquisitionService> Prepare(ManagerTestHost host)
    {
        var ct = TestContext.Current.CancellationToken;
        var sessions = host.Services.GetRequiredService<WorkSessionProvider>();
        await sessions.RefreshAsync(ct);
        var adapter = host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        await host.Services.GetRequiredService<QueueCredentialProvider>()
            .StepAsync(() => host.Services.GetRequiredService<AdmissionGate>().TryAdmit() ? sessions.Current : null, () => adapter.Capabilities, ct);
        return host.Services.GetRequiredService<WorkAcquisitionService>();
    }

    internal static void Enqueue(FakeQueue queue, FakePlatform platform, int count)
    {
        for (var i = 0; i < count; i++)
        {
            queue.Ready.Enqueue(new InMemoryDelivery(platform, Payload(i)));
        }
    }

    [Fact]
    public async Task No_fetch_while_not_admitted()
    {
        var (host, queue) = Host(SimulatedOutcome.Succeed, 2, 2);
        await using var _ = host;
        Enqueue(queue, host.Platform, 1);

        var service = host.Services.GetRequiredService<WorkAcquisitionService>();
        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        await Restore(host, paused: true);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        queue.Fetches.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_fetch_per_free_slot_up_to_the_effective_limit()
    {
        var (host, queue) = Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 2);
        await using var _ = host;
        await Restore(host);
        var service = await Prepare(host);
        (await host.Control.SetHostLocalConcurrencyAsync(2)).Succeeded.ShouldBeTrue();
        Enqueue(queue, host.Platform, 3);

        service.EffectiveLimit().ShouldBe(2);
        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        host.Services.GetRequiredService<AttemptRegistry>().ActiveCount.ShouldBe(2);

        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        queue.Fetches.Count.ShouldBe(2);
        queue.Ready.Count.ShouldBe(1);
        await host.Services.GetRequiredService<AttemptRegistry>().StopAndCleanUpAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<AttemptRegistry>().ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Only_validated_capabilities_are_pulled()
    {
        var (host, queue) = Host(SimulatedOutcome.Succeed, 2, 2);
        await using var _ = host;
        await Restore(host);
        var service = await Prepare(host);

        for (var i = 0; i < 4; i++)
        {
            (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        }

        queue.Fetches.ShouldNotBeEmpty();
        queue.Fetches.ShouldAllBe(c => c == Detection);
    }

    [Fact]
    public async Task Attempt_lifecycle_leaves_no_secret_in_logs_or_state()
    {
        var (host, queue) = Host(SimulatedOutcome.Succeed, 2, 2);
        await using var _ = host;
        await Restore(host);
        var service = await Prepare(host);
        var credential = host.Services.GetRequiredService<QueueCredentialProvider>().Current!;
        var seed = credential.Seed;
        Enqueue(queue, host.Platform, 1);

        (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        await host.Services.GetRequiredService<AttemptRegistry>().WaitForIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        host.Platform.Headers.Count(h => h.Operation == WorkOperation.Completion).ShouldBe(1);
        var secrets = new List<string> { Jwt, seed, "opaque-token", "918273645" };
        var logs = string.Join('\n', host.Logs.GetSnapshot().Select(l => l.Message + " " + l.Exception));
        var files = Directory.EnumerateFiles(host.StateDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f)));
        foreach (var secret in secrets)
        {
            logs.Contains(secret, StringComparison.Ordinal).ShouldBeFalse("log output leaks a secret");
            files.ShouldAllBe(f => !f.Contains(secret, StringComparison.Ordinal));
        }
    }
}
