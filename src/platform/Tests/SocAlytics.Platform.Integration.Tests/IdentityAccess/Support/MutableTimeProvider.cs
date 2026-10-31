using System.Diagnostics;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

public sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;
    private long _anchor = Stopwatch.GetTimestamp();
    private bool _followsRealTime;

    public MutableTimeProvider()
        : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _followsRealTime ? _now + Stopwatch.GetElapsedTime(_anchor) : _now;
        }
    }

    // When the clock follows real time, it keeps running from the set value.
    public void Set(DateTimeOffset now)
    {
        lock (_gate)
        {
            _now = now;
            _anchor = Stopwatch.GetTimestamp();
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }

    // Adds the real time elapsed since the last Set, so a stored timestamp or retry time never depends on a frozen reading.
    public void FollowRealTime()
    {
        lock (_gate)
        {
            _anchor = Stopwatch.GetTimestamp();
            _followsRealTime = true;
        }
    }
}
