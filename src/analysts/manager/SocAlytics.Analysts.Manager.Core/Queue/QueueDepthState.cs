using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Queue;

public enum QueueDepthStatus
{
    NotYetAvailable,
    Available,
    Stale,
    NoCapability,
    Unavailable,
}

public sealed record QueueDepthSnapshot(
    QueueDepthStatus State, DateTimeOffset? ObservedAt, DateTimeOffset? ReceivedAt,
    IReadOnlyList<QueueDepthCapability> Capabilities, long Total, string? LastError);

/// <summary>
/// Latest queue depth observation, in memory only. Age is measured on the Manager's clock (<c>ReceivedAt</c>),
/// never against the platform's <c>observedAt</c>, so clock skew cannot make an observation stale or fresh.
/// </summary>
public sealed class QueueDepthState(TimeProvider time, IOptions<PlatformOptions> options)
{
    private readonly object _gate = new();
    private QueueDepthResponse? _observation;
    private DateTimeOffset _receivedAt;
    private bool _noCapability;
    private string? _lastError;
    private TimeSpan? _interval;

    /// <summary>The status check interval the platform currently asks for; defaults to the configured one.</summary>
    public TimeSpan StatusCheckInterval
    {
        get { lock (_gate) { return _interval ?? options.Value.StatusCheckInterval; } }
        set { lock (_gate) { _interval = value; } }
    }

    public QueueDepthSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_noCapability)
            {
                return new(QueueDepthStatus.NoCapability, null, null, [], 0, null);
            }

            if (_observation is null)
            {
                return _lastError is null
                    ? new(QueueDepthStatus.NotYetAvailable, null, null, [], 0, null)
                    : new(QueueDepthStatus.Unavailable, null, null, [], 0, _lastError);
            }

            var interval = _interval ?? options.Value.StatusCheckInterval;
            var stale = _lastError is not null || time.GetUtcNow() - _receivedAt > 2 * interval;
            return new(
                stale ? QueueDepthStatus.Stale : QueueDepthStatus.Available,
                _observation.ObservedAt, _receivedAt, _observation.Capabilities, _observation.Total, _lastError);
        }
    }

    public void RecordSuccess(QueueDepthResponse observation)
    {
        lock (_gate)
        {
            _observation = observation;
            _receivedAt = time.GetUtcNow();
            _noCapability = false;
            _lastError = null;
        }
    }

    public void RecordFailure(string error)
    {
        lock (_gate)
        {
            _noCapability = false;
            _lastError = error;
        }
    }

    public void MarkNoCapability()
    {
        lock (_gate)
        {
            _observation = null;
            _noCapability = true;
            _lastError = null;
        }
    }

    /// <summary>Forgets everything: revocation, unregistration, or a registration that is not active.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _observation = null;
            _noCapability = false;
            _lastError = null;
        }
    }
}
