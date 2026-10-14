using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

public enum HeartbeatEnd
{
    /// <summary>The caller cancelled the loop.</summary>
    Stopped,

    /// <summary>The platform rejected the heartbeat: the attempt is fenced, stale, or obsolete.</summary>
    Fenced,

    /// <summary>The registration is inactive or no session is available.</summary>
    Inactive,

    /// <summary>No renewal was confirmed before the last confirmed lease expired.</summary>
    LeaseLost,
}

/// <summary>Renews the lease at lease ÷ 3 and never assumes a renewal that the platform did not confirm.</summary>
public sealed class HeartbeatLoop(PlatformClient platform, TimeProvider time, ILogger? logger = null)
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public static TimeSpan Interval(TimeSpan lease) => lease / 3;

    public async Task<HeartbeatEnd> RunAsync(
        ActiveAttempt attempt, Func<PlatformSession?> sessions, CancellationToken cancellationToken)
    {
        var interval = Interval(attempt.LeaseDuration);
        var backoff = InitialBackoff;
        var wait = interval;
        try
        {
            while (true)
            {
                await Task.Delay(wait, time, cancellationToken);
                var session = sessions();
                if (session is null)
                {
                    return HeartbeatEnd.Inactive;
                }

                var result = await platform.SendHeartbeatAsync(
                    session, attempt.AttemptId, attempt.LogicalJobId, attempt.FencingToken, time.GetUtcNow(),
                    attempt.ExecutionState, cancellationToken);
                switch (result.Kind)
                {
                    case WorkResultKind.Success:
                    case WorkResultKind.Replayed:
                        attempt.ConfirmHeartbeat(time.GetUtcNow(), result.Value!.LeaseExpiresAt);
                        backoff = InitialBackoff;
                        wait = interval;
                        continue;
                    case WorkResultKind.Inactive:
                        _logger.LogWarning(
                            "Heartbeat stopped for Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}: registration inactive",
                            attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                        return HeartbeatEnd.Inactive;
                    case WorkResultKind.Transient:
                        break;
                    default:
                        _logger.LogWarning(
                            "Attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} was fenced: heartbeat rejected ({Code})",
                            attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId, result.Code ?? result.Kind.ToString());
                        return HeartbeatEnd.Fenced;
                }

                var remaining = attempt.LeaseExpiresAt - time.GetUtcNow();
                _logger.LogWarning(
                    "Heartbeat failed for Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}; lease remaining {RemainingSeconds}s",
                    attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, Math.Max(0, (int)remaining.TotalSeconds));
                if (remaining <= TimeSpan.Zero)
                {
                    _logger.LogWarning(
                        "Lease lost for Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}: no renewal confirmed",
                        attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                    return HeartbeatEnd.LeaseLost;
                }

                // The next try must still precede the lease expiry known from the last confirmed renewal.
                wait = TimeSpan.FromTicks(Math.Min(backoff.Ticks, remaining.Ticks / 2));
                backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HeartbeatEnd.Stopped;
        }
    }
}
