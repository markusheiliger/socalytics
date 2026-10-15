using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Queue;

public class QueueDepthStateTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
    private readonly QueueDepthState _state;

    public QueueDepthStateTests()
    {
        var options = new PlatformOptions { StatusCheckInterval = TimeSpan.FromSeconds(60) };
        _state = new QueueDepthState(_time, Options.Create(options));
    }

    private static QueueDepthResponse Observation(DateTimeOffset observedAt, int waiting = 5) =>
        new(observedAt, [new QueueDepthCapability("person-detection", waiting)], waiting);

    [Fact]
    public void Starts_as_not_yet_available()
    {
        var snapshot = _state.Snapshot();
        snapshot.State.ShouldBe(QueueDepthStatus.NotYetAvailable);
        snapshot.Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public void Success_makes_the_observation_available_and_replaces_the_previous_one()
    {
        _state.RecordSuccess(Observation(_time.GetUtcNow(), 5));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
        _state.Snapshot().Total.ShouldBe(5);

        _state.RecordSuccess(Observation(_time.GetUtcNow(), 2));
        var snapshot = _state.Snapshot();
        snapshot.Total.ShouldBe(2);
        snapshot.Capabilities.Single().Waiting.ShouldBe(2);
        snapshot.ReceivedAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public void Observation_becomes_stale_after_two_status_check_intervals_on_the_manager_clock()
    {
        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _time.Advance(TimeSpan.FromSeconds(120));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
        _time.Advance(TimeSpan.FromSeconds(1));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Stale);
    }

    [Fact]
    public void Platform_interval_changes_the_stale_threshold()
    {
        _state.StatusCheckInterval = TimeSpan.FromSeconds(10);
        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _time.Advance(TimeSpan.FromSeconds(21));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Stale);
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void Platform_clock_skew_does_not_change_the_state(int skewSeconds)
    {
        _state.RecordSuccess(Observation(_time.GetUtcNow().AddSeconds(skewSeconds)));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
        _time.Advance(TimeSpan.FromSeconds(121));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Stale);
    }

    [Fact]
    public void Failure_without_observation_is_unavailable_and_with_observation_stale()
    {
        _state.RecordFailure("platform-unavailable");
        var unavailable = _state.Snapshot();
        unavailable.State.ShouldBe(QueueDepthStatus.Unavailable);
        unavailable.LastError.ShouldBe("platform-unavailable");

        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
        _state.RecordFailure("platform-unavailable");
        var stale = _state.Snapshot();
        stale.State.ShouldBe(QueueDepthStatus.Stale);
        stale.Total.ShouldBe(5);

        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
        _state.Snapshot().LastError.ShouldBeNull();
    }

    [Fact]
    public void No_assignment_is_no_capability_and_drops_the_observation()
    {
        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _state.MarkNoCapability();
        var snapshot = _state.Snapshot();
        snapshot.State.ShouldBe(QueueDepthStatus.NoCapability);
        snapshot.ObservedAt.ShouldBeNull();

        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);
    }

    [Fact]
    public void Clear_returns_to_not_yet_available()
    {
        _state.RecordSuccess(Observation(_time.GetUtcNow()));
        _state.RecordFailure("x");
        _state.Clear();
        var snapshot = _state.Snapshot();
        snapshot.State.ShouldBe(QueueDepthStatus.NotYetAvailable);
        snapshot.LastError.ShouldBeNull();
        snapshot.Total.ShouldBe(0);
    }
}
