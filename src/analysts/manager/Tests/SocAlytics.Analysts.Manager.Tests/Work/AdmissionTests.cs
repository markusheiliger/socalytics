#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public sealed class AdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Count(ManagerTestHost host, WorkOperation operation) =>
        host.Platform.Headers.Count(h => h.Operation == operation);

    private static AttemptRegistry Registry(ManagerTestHost host) => host.Services.GetRequiredService<AttemptRegistry>();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Pause_stops_new_claims_at_once_while_active_attempts_complete()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        (await host.Control.SetHostLocalConcurrencyAsync(2)).Succeeded.ShouldBeTrue();
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 2);

        (await service.StepAsync(Ct)).ShouldBeTrue();
        (await host.Control.PauseAsync()).Succeeded.ShouldBeTrue();
        (await service.StepAsync(Ct)).ShouldBeFalse();

        queue.Fetches.Count.ShouldBe(1);
        queue.Ready.Count.ShouldBe(1);
        Registry(host).ActiveCount.ShouldBe(1);
        await Registry(host).StopAndCleanUpAsync(Ct);
    }

    [Fact]
    public async Task Timed_pause_resumes_at_its_end_time()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
        (await host.Control.PauseAsync(TimeSpan.FromMinutes(2))).Succeeded.ShouldBeTrue();

        (await service.StepAsync(Ct)).ShouldBeFalse();
        queue.Fetches.ShouldBeEmpty();

        host.Time.Advance(TimeSpan.FromMinutes(2));
        (await host.Services.GetRequiredService<TimedPauseScheduler>().CheckAsync()).ShouldBeTrue();

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Count(host, WorkOperation.Claim).ShouldBe(1);
    }

    [Fact]
    public async Task Runtime_unavailable_stops_pulling_but_keeps_heartbeating()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 4, stampMax: 4);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        (await host.Control.SetHostLocalConcurrencyAsync(2)).Succeeded.ShouldBeTrue();
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 2);
        (await service.StepAsync(Ct)).ShouldBeTrue();
        await WaitUntilAsync(() => Registry(host).Attempts.Any(a => a.ExecutionState == AttemptExecutionStates.Running));

        var adapter = (SimulatedRuntimeAdapter)host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        adapter.Failure = new IOException("gone");
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(Ct);
        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");

        (await service.StepAsync(Ct)).ShouldBeFalse();
        queue.Fetches.Count.ShouldBe(1);
        Registry(host).ActiveCount.ShouldBe(1);

        var before = Count(host, WorkOperation.Heartbeat);
        host.Time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => Count(host, WorkOperation.Heartbeat) > before);
        Registry(host).ActiveCount.ShouldBe(1);
        await Registry(host).StopAndCleanUpAsync(Ct);
    }

    [Fact]
    public async Task Unfit_deliveries_are_negatively_acknowledged_with_the_delay_and_never_claimed()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        var delivery = new InMemoryDelivery(host.Platform, WorkAcquisitionTests.Payload(0, memoryMebibytes: long.MaxValue / 2));
        queue.Ready.Enqueue(delivery);

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var expected = host.Services.GetRequiredService<QueueCredentialProvider>().Current!.UnfitRedeliveryDelay;
        delivery.NakDelays.ShouldBe([expected]);
        delivery.Acks.ShouldBe(0);
        Count(host, WorkOperation.Claim).ShouldBe(0);
    }

    [Fact]
    public async Task Delivery_fetched_while_admission_ended_is_negatively_acknowledged_without_delay()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        var delivery = new InMemoryDelivery(host.Platform, WorkAcquisitionTests.Payload(0));
        var queue = new PausingQueue(delivery, () => host.Control.PauseAsync());
        await WorkAcquisitionTests.Restore(host);
        var service = await Prepare(host, queue);

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        delivery.PlainNaks.ShouldBe(1);
        delivery.NakDelays.ShouldBeEmpty();
        delivery.Acks.ShouldBe(0);
        Count(host, WorkOperation.Claim).ShouldBe(0);
    }

    private static async Task<WorkAcquisitionService> Prepare(ManagerTestHost host, IQueueConnection queue)
    {
        var service = ActivatorUtilities.CreateInstance<WorkAcquisitionService>(host.Services, queue);
        await host.Services.GetRequiredService<WorkSessionProvider>().RefreshAsync(Ct);
        var adapter = host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        var sessions = host.Services.GetRequiredService<WorkSessionProvider>();
        await host.Services.GetRequiredService<QueueCredentialProvider>().StepAsync(
            () => host.Services.GetRequiredService<AdmissionGate>().TryAdmit() ? sessions.Current : null, () => adapter.Capabilities, Ct);
        return service;
    }

    private sealed class PausingQueue(IQueueDelivery delivery, Func<Task> onFetch) : IQueueConnection
    {
        public async Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken)
        {
            await onFetch();
            return delivery;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
