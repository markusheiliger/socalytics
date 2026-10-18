#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
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

public sealed class MinimumVersionAdmissionTests
{
    private const string AboveBuild = "99.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Count(ManagerTestHost host, WorkOperation operation) =>
        host.Platform.Headers.Count(h => h.Operation == operation);

    private static MinimumVersionState Minimum(ManagerTestHost host) => host.Services.GetRequiredService<MinimumVersionState>();

    private static AttemptRegistry Registry(ManagerTestHost host) => host.Services.GetRequiredService<AttemptRegistry>();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(20, Ct);
        }
    }

    private static async Task RenewCredentialsAsync(ManagerTestHost host)
    {
        var sessions = host.Services.GetRequiredService<WorkSessionProvider>();
        var adapter = host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        await sessions.RefreshAsync(Ct);
        await host.Services.GetRequiredService<QueueCredentialProvider>().StepAsync(
            () => host.Services.GetRequiredService<AdmissionGate>().TryAdmit() ? sessions.Current : null, () => adapter.Capabilities, Ct);
    }

    [Fact]
    public async Task Below_the_minimum_no_credential_is_requested_and_nothing_is_claimed_until_it_is_lowered()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        Minimum(host).Update(AboveBuild);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
        var service = host.Services.GetRequiredService<WorkAcquisitionService>();

        await RenewCredentialsAsync(host);

        Count(host, WorkOperation.QueueCredentials).ShouldBe(0);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
        (await service.StepAsync(Ct)).ShouldBeFalse();
        queue.Fetches.ShouldBeEmpty();
        Count(host, WorkOperation.Claim).ShouldBe(0);

        Minimum(host).Update(null);
        await RenewCredentialsAsync(host);

        Count(host, WorkOperation.QueueCredentials).ShouldBe(1);
        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Count(host, WorkOperation.Claim).ShouldBe(1);
    }

    [Fact]
    public async Task A_job_delivered_while_below_is_negatively_acknowledged_and_never_claimed()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        await RenewCredentialsAsync(host);
        var delivery = new InMemoryDelivery(host.Platform, WorkAcquisitionTests.Payload(0));
        var service = ActivatorUtilities.CreateInstance<WorkAcquisitionService>(
            host.Services, new BelowOnFetchQueue(delivery, () => Minimum(host).Update(AboveBuild)));

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        delivery.PlainNaks.ShouldBe(1);
        delivery.Acks.ShouldBe(0);
        Count(host, WorkOperation.Claim).ShouldBe(0);
    }

    [Fact]
    public async Task A_running_attempt_keeps_heartbeating_while_below()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
        (await service.StepAsync(Ct)).ShouldBeTrue();
        await WaitUntilAsync(() => Registry(host).Attempts.Any(a => a.ExecutionState == AttemptExecutionStates.Running));

        Minimum(host).Update(AboveBuild);
        var before = Count(host, WorkOperation.Heartbeat);
        host.Time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => Count(host, WorkOperation.Heartbeat) > before);

        Registry(host).ActiveCount.ShouldBe(1);
        (await service.StepAsync(Ct)).ShouldBeFalse();
        await Registry(host).StopAndCleanUpAsync(Ct);
    }

    [Fact]
    public async Task A_running_attempt_completes_when_the_minimum_rises_after_its_claim()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        host.Platform.Script(WorkOperation.Claim, () =>
        {
            Minimum(host).Update(AboveBuild);
            return WorkAcquisitionTests.Json(System.Net.HttpStatusCode.Created,
                $$"""{"attemptId":"{{Guid.NewGuid()}}","logicalJobId":"{{Guid.NewGuid()}}","readinessOccurrenceId":"{{Guid.NewGuid()}}","attemptNumber":1,"fencingToken":918273645,"leaseExpiresAt":"{{host.Time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}""");
        });
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Minimum(host).IsBelow.ShouldBeTrue();
        Count(host, WorkOperation.Claim).ShouldBe(1);
        Count(host, WorkOperation.Completion).ShouldBe(1);
    }

    [Fact]
    public async Task A_paused_state_is_kept_while_below_and_after_the_minimum_is_lowered()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        (await host.Control.PauseAsync()).Succeeded.ShouldBeTrue();

        Minimum(host).Update(AboveBuild);
        host.Control.GetStatus().OperatingState.ShouldBe("paused");

        Minimum(host).Update(null);
        host.Control.GetStatus().OperatingState.ShouldBe("paused");
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }

    [Fact]
    public async Task A_draining_state_is_kept_while_below_and_after_the_minimum_is_lowered()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var machine = host.Services.GetRequiredService<RegistrationCoordinator>().Machine!;
        machine.BeginDrain(DrainPurpose.SafeExit, host.Time.GetUtcNow().AddMinutes(5)).ShouldBeTrue();

        Minimum(host).Update(AboveBuild);
        host.Control.GetStatus().OperatingState.ShouldBe("draining");

        Minimum(host).Update(null);
        host.Control.GetStatus().OperatingState.ShouldBe("draining");
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }

    private sealed class BelowOnFetchQueue(IQueueDelivery delivery, Action onFetch) : IQueueConnection
    {
        public Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken)
        {
            onFetch();
            return Task.FromResult<IQueueDelivery?>(delivery);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
