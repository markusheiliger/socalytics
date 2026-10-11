using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Operating;

public class DrainCoordinatorTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(30);

    private sealed record Rig(
        FakeTimeProvider Time, OperatingStateMachine Machine, AdmissionGate Gate, SimulatedWork Work, DrainCoordinator Drain);

    private static Rig Create(DrainTimeoutPolicy policy, OperatingStateKind prior)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var machine = new OperatingStateMachine(OperatingIntent.Running);
        if (prior == OperatingStateKind.RuntimeUnavailable)
        {
            machine.PreflightFailed();
        }
        else
        {
            machine.PreflightPassed();
        }

        if (prior == OperatingStateKind.Paused)
        {
            machine.Pause();
        }

        var gate = new AdmissionGate();
        gate.Attach(machine);
        var work = new SimulatedWork();
        var options = Options.Create(new OperatingOptions { DrainTimeout = DrainTimeout, DrainTimeoutPolicy = policy, CleanupBound = Cleanup });
        return new Rig(time, machine, gate, work, new DrainCoordinator(gate, work, options, time));
    }

    [Fact]
    public async Task Work_finishing_before_deadline_completes_and_admits_nothing()
    {
        var r = Create(DrainTimeoutPolicy.CancelRequest, OperatingStateKind.Running);
        r.Work.Start();
        var drain = r.Drain.DrainAsync(DrainPurpose.Unregister, TestContext.Current.CancellationToken);
        r.Machine.State.Kind.ShouldBe(OperatingStateKind.Draining);
        r.Machine.State.Purpose.ShouldBe(DrainPurpose.Unregister);
        r.Machine.State.Deadline.ShouldBe(r.Time.GetUtcNow() + DrainTimeout);
        r.Gate.TryAdmit().ShouldBeFalse();
        r.Time.Advance(TimeSpan.FromMinutes(1));
        r.Work.Finish();
        (await drain).ShouldBe(DrainResult.Completed);
        r.Gate.TryAdmit().ShouldBeFalse();
    }

    [Theory]
    [InlineData(OperatingStateKind.Running)]
    [InlineData(OperatingStateKind.Paused)]
    [InlineData(OperatingStateKind.RuntimeUnavailable)]
    public async Task CancelRequest_restores_prior_state_with_intent_unchanged(OperatingStateKind prior)
    {
        var r = Create(DrainTimeoutPolicy.CancelRequest, prior);
        var intent = r.Machine.Intent;
        r.Work.Start();
        var drain = r.Drain.DrainAsync(DrainPurpose.Unregister, TestContext.Current.CancellationToken);
        r.Time.Advance(DrainTimeout);
        (await drain).ShouldBe(DrainResult.Cancelled);
        r.Machine.State.Kind.ShouldBe(prior);
        r.Machine.Intent.ShouldBe(intent);
        r.Gate.TryAdmit().ShouldBe(prior == OperatingStateKind.Running);
    }

    [Fact]
    public async Task StopAndCleanUp_stops_work_and_completes_within_bound()
    {
        var r = Create(DrainTimeoutPolicy.StopAndCleanUp, OperatingStateKind.Running);
        r.Work.Start();
        var started = r.Time.GetUtcNow();
        var drain = r.Drain.DrainAsync(DrainPurpose.SafeExit, TestContext.Current.CancellationToken);
        r.Time.Advance(DrainTimeout);
        (await drain).ShouldBe(DrainResult.Completed);
        r.Work.Stopped.ShouldBeTrue();
        (r.Time.GetUtcNow() - started).ShouldBeLessThanOrEqualTo(DrainTimeout + Cleanup);
        r.Machine.State.Kind.ShouldNotBe(OperatingStateKind.Draining);
    }

    [Theory]
    [InlineData(DrainTimeoutPolicy.CancelRequest)]
    [InlineData(DrainTimeoutPolicy.StopAndCleanUp)]
    public async Task Never_draining_beyond_timeout_plus_cleanup_bound(DrainTimeoutPolicy policy)
    {
        var r = Create(policy, OperatingStateKind.Running);
        r.Work.Start();
        var drain = r.Drain.DrainAsync(DrainPurpose.SessionEnd, TestContext.Current.CancellationToken);
        r.Time.Advance(DrainTimeout + Cleanup);
        await drain;
        r.Machine.State.Kind.ShouldNotBe(OperatingStateKind.Draining);
    }

    [Fact]
    public async Task Revocation_during_drain_ends_it_immediately()
    {
        var r = Create(DrainTimeoutPolicy.CancelRequest, OperatingStateKind.Running);
        r.Work.Start();
        var drain = r.Drain.DrainAsync(DrainPurpose.Unregister, TestContext.Current.CancellationToken);
        r.Machine.Revoked();
        (await drain).ShouldBe(DrainResult.Revoked);
        r.Gate.TryAdmit().ShouldBeFalse();
    }
}
