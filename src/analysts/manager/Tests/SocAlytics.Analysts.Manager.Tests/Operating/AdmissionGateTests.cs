using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.Tests.Operating;

public class AdmissionGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Closed_before_confirmation() => new AdmissionGate().TryAdmit().ShouldBeFalse();

    [Fact]
    public void Closed_until_preflight_then_open_then_closed_after_pause()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        var g = new AdmissionGate();
        g.Attach(m);
        g.TryAdmit().ShouldBeFalse();
        m.PreflightPassed();
        g.TryAdmit().ShouldBeTrue();
        m.Pause();
        g.TryAdmit().ShouldBeFalse();
    }

    [Fact]
    public void Unregister_hold_closes_gate_until_released()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        var g = new AdmissionGate();
        g.Attach(m);
        m.PreflightPassed();
        g.HoldForUnregister();
        g.TryAdmit().ShouldBeFalse();
        g.ReleaseUnregisterHold();
        g.TryAdmit().ShouldBeTrue();
    }

    [Fact]
    public void Closed_when_paused_draining_unavailable_ended_or_detached()
    {
        var m = new OperatingStateMachine(OperatingIntent.Paused);
        var g = new AdmissionGate();
        g.Attach(m);
        m.PreflightPassed();
        g.TryAdmit().ShouldBeFalse();
        m.BeginDrain(DrainPurpose.SafeExit, Now);
        g.TryAdmit().ShouldBeFalse();
        m.DrainCancelled();
        m.Resume();
        g.TryAdmit().ShouldBeTrue();
        m.PreflightFailed();
        g.TryAdmit().ShouldBeFalse();
        m.PreflightPassed();
        g.TryAdmit().ShouldBeTrue();
        g.Detach();
        g.TryAdmit().ShouldBeFalse();
        g.Attach(m);
        m.Revoked();
        g.TryAdmit().ShouldBeFalse();
    }
}
