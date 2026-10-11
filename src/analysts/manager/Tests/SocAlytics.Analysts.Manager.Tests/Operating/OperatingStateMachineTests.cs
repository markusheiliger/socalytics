using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.Tests.Operating;

public class OperatingStateMachineTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 10, 13, 0, 0, TimeSpan.Zero);

    private static OperatingStateKind Kind(OperatingStateMachine m) => m.State.Kind;

    private static OperatingStateMachine Running()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        m.PreflightPassed();
        return m;
    }

    [Fact]
    public void Starts_runtime_unavailable() =>
        Kind(new OperatingStateMachine(OperatingIntent.Running)).ShouldBe(OperatingStateKind.RuntimeUnavailable);

    [Fact]
    public void Preflight_passed_follows_intent()
    {
        Kind(Running()).ShouldBe(OperatingStateKind.Running);
        var p = new OperatingStateMachine(OperatingIntent.Paused);
        p.PreflightPassed();
        Kind(p).ShouldBe(OperatingStateKind.Paused);
        var t = new OperatingStateMachine(OperatingIntent.PausedUntil(End));
        t.PreflightPassed();
        Kind(t).ShouldBe(OperatingStateKind.Paused);
    }

    [Fact]
    public void Preflight_failed_and_runtime_lost_enter_runtime_unavailable()
    {
        var m = Running();
        m.PreflightFailed().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.RuntimeUnavailable);
        var p = new OperatingStateMachine(OperatingIntent.Paused);
        p.PreflightPassed();
        p.RuntimeLost().ShouldBeTrue();
        Kind(p).ShouldBe(OperatingStateKind.RuntimeUnavailable);
    }

    [Fact]
    public void Pause_and_resume_when_preflight_passed()
    {
        var m = Running();
        m.Pause().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.Paused);
        m.Intent.Kind.ShouldBe(OperatingIntentKind.Paused);
        m.Resume().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.Running);
        m.PauseUntil(End).ShouldBeTrue();
        m.Intent.EndUtc.ShouldBe(End);
        m.TimedPauseEnded().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.Running);
        m.Intent.Kind.ShouldBe(OperatingIntentKind.Running);
    }

    [Fact]
    public void Pause_in_runtime_unavailable_records_intent_only()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        m.Pause().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.RuntimeUnavailable);
        m.Intent.Kind.ShouldBe(OperatingIntentKind.Paused);
        m.PreflightPassed();
        Kind(m).ShouldBe(OperatingStateKind.Paused);
    }

    [Fact]
    public void Resume_without_passed_preflight_stays_unavailable_then_runs()
    {
        var m = new OperatingStateMachine(OperatingIntent.Paused);
        m.Resume().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.RuntimeUnavailable);
        m.PreflightPassed();
        Kind(m).ShouldBe(OperatingStateKind.Running);
    }

    [Fact]
    public void Timed_pause_end_after_failed_preflight_does_not_run()
    {
        var m = Running();
        m.PauseUntil(End);
        m.PreflightFailed();
        m.TimedPauseEnded().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.RuntimeUnavailable);
        m.Intent.Kind.ShouldBe(OperatingIntentKind.Running);
    }

    [Fact]
    public void Timed_pause_ended_refused_without_timed_intent()
    {
        Running().TimedPauseEnded().ShouldBeFalse();
    }

    [Theory]
    [InlineData(OperatingStateKind.Running)]
    [InlineData(OperatingStateKind.Paused)]
    [InlineData(OperatingStateKind.RuntimeUnavailable)]
    public void Drain_cancel_returns_to_prior_with_intent_unchanged(OperatingStateKind prior)
    {
        var m = new OperatingStateMachine(prior == OperatingStateKind.Paused ? OperatingIntent.Paused : OperatingIntent.Running);
        if (prior != OperatingStateKind.RuntimeUnavailable)
        {
            m.PreflightPassed();
        }

        var intent = m.Intent;
        m.BeginDrain(DrainPurpose.Unregister, End).ShouldBeTrue();
        m.State.ShouldBe(new OperatingState(OperatingStateKind.Draining, DrainPurpose.Unregister, prior, End));
        m.DrainCancelled().ShouldBeTrue();
        Kind(m).ShouldBe(prior);
        m.Intent.ShouldBe(intent);
    }

    [Fact]
    public void Drain_completed_ends_state()
    {
        var m = Running();
        m.BeginDrain(DrainPurpose.SafeExit, End);
        m.DrainCompleted().ShouldBeTrue();
        Kind(m).ShouldBe(OperatingStateKind.Ended);
    }

    [Fact]
    public void Revoked_supersedes_every_state()
    {
        var states = new Action<OperatingStateMachine>[]
        {
            _ => { },
            m => m.PreflightPassed(),
            m => { m.PreflightPassed(); m.Pause(); },
            m => m.BeginDrain(DrainPurpose.SessionEnd, End),
        };
        foreach (var setup in states)
        {
            var m = new OperatingStateMachine(OperatingIntent.Running);
            setup(m);
            m.Revoked().ShouldBeTrue();
            Kind(m).ShouldBe(OperatingStateKind.Ended);
            m.IsRevoked.ShouldBeTrue();
        }
    }

    [Fact]
    public void Refusals()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        m.DrainCancelled().ShouldBeFalse();
        m.DrainCompleted().ShouldBeFalse();
        m.BeginDrain(DrainPurpose.SafeExit, End).ShouldBeTrue();
        m.BeginDrain(DrainPurpose.SafeExit, End).ShouldBeFalse();
        m.Pause().ShouldBeFalse();
        m.Resume().ShouldBeFalse();
        m.DrainCompleted();
        m.PreflightPassed().ShouldBeFalse();
        m.Pause().ShouldBeFalse();
        m.BeginDrain(DrainPurpose.SafeExit, End).ShouldBeFalse();
    }

    [Fact]
    public void Raises_change_event_only_on_change()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running);
        var count = 0;
        m.Changed += (_, _) => count++;
        m.PreflightPassed();
        m.PreflightPassed();
        m.Pause();
        count.ShouldBe(2);
    }

    [Fact]
    public void Pending_unregister_flag_is_kept()
    {
        var m = new OperatingStateMachine(OperatingIntent.Running with { PendingUnregister = true });
        m.PreflightPassed();
        m.Pause();
        m.Intent.PendingUnregister.ShouldBeTrue();
    }
}
