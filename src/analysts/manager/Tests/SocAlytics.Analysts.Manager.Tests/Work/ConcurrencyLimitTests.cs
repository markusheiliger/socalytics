using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public sealed class ConcurrencyLimitTests
{
    private static async Task<int> PullAllAsync(WorkAcquisitionService service)
    {
        var started = 0;
        while (await service.StepAsync(TestContext.Current.CancellationToken))
        {
            started++;
        }

        return started;
    }

    [Fact]
    public async Task Never_more_active_attempts_than_the_effective_limit()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        (await host.Control.SetHostLocalConcurrencyAsync(2)).Succeeded.ShouldBeTrue();
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 6);
        var registry = host.Services.GetRequiredService<AttemptRegistry>();

        (await PullAllAsync(service)).ShouldBe(2);

        registry.ActiveCount.ShouldBe(2);
        queue.Ready.Count.ShouldBe(4);
        await registry.StopAndCleanUpAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Default_host_local_limit_is_one()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 3);

        host.Control.GetConcurrency().ShouldBe(new ConcurrencySnapshot(1, 4, 4, 1));
        (await PullAllAsync(service)).ShouldBe(1);
        await host.Services.GetRequiredService<AttemptRegistry>().StopAndCleanUpAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lowering_never_cancels_and_raising_pulls_more()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        var registry = host.Services.GetRequiredService<AttemptRegistry>();
        await host.Control.SetHostLocalConcurrencyAsync(3);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 6);
        (await PullAllAsync(service)).ShouldBe(3);

        await host.Control.SetHostLocalConcurrencyAsync(1);

        registry.ActiveCount.ShouldBe(3);
        (await PullAllAsync(service)).ShouldBe(0);
        registry.ActiveCount.ShouldBe(3);

        await host.Control.SetHostLocalConcurrencyAsync(4);
        (await PullAllAsync(service)).ShouldBe(1);
        registry.ActiveCount.ShouldBe(4);
        await registry.StopAndCleanUpAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_local_limit_is_persisted_in_the_signed_state()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);

        (await host.Control.SetHostLocalConcurrencyAsync(3)).Succeeded.ShouldBeTrue();

        var read = await host.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken);
        read.State!.HostLocalConcurrency.ShouldBe(3);
    }

    [Fact]
    public async Task Changed_stamp_maximum_applies_after_the_next_credential_renewal()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        await host.Control.SetHostLocalConcurrencyAsync(4);
        service.EffectiveLimit().ShouldBe(2);

        host.Platform.Script(WorkOperation.QueueCredentials, () => WorkAcquisitionTests.Json(HttpStatusCode.Created,
            $$"""{"userJwt":"jwt","expiresAt":"{{host.Time.GetUtcNow().AddHours(2):O}}","queueUrl":"tls://queue.test:4222","stream":"SOCALYTICS_ANALYSIS_JOBS","consumers":[{"capability":"player-detection","consumer":"analyst-player-detection"}],"inboxPrefix":"_INBOX_abc","maxConcurrentAnalysts":3}"""));
        service.EffectiveLimit().ShouldBe(2);

        host.Time.Advance(TimeSpan.FromMinutes(41));
        var sessions = host.Services.GetRequiredService<WorkSessionProvider>();
        await sessions.RefreshAsync(TestContext.Current.CancellationToken);
        var adapter = host.Services.GetRequiredService<SocAlytics.Analysts.Manager.Runtime.RuntimeAdapterCatalog>()
            .Find(SimulatedRuntimeAdapter.TestProfile)!;
        await host.Services.GetRequiredService<QueueCredentialProvider>()
            .StepAsync(() => host.Services.GetRequiredService<AdmissionGate>().TryAdmit() ? sessions.Current : null, () => adapter.Capabilities, TestContext.Current.CancellationToken);

        service.EffectiveLimit().ShouldBe(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Invalid_values_are_refused_and_nothing_changes(int value)
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        await host.Control.SetHostLocalConcurrencyAsync(2);

        var result = await host.Control.SetHostLocalConcurrencyAsync(value);

        result.Succeeded.ShouldBeFalse();
        result.Code.ShouldBe("invalid-concurrency");
        host.Control.GetConcurrency().HostLocal.ShouldBe(2);
        var read = await host.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken);
        read.State!.HostLocalConcurrency.ShouldBe(2);
    }
}
