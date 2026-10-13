using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

public static class AttemptExecutionStates
{
    public const string Preparing = "preparing";
    public const string Running = "running";
    public const string Finalizing = "finalizing";
}

/// <summary>In-memory view of one claimed attempt; the fencing token is never logged or persisted.</summary>
public sealed class ActiveAttempt
{
    private readonly object _gate = new();
    private DateTimeOffset _leaseExpiresAt;
    private DateTimeOffset? _lastConfirmedHeartbeat;
    private string _executionState = AttemptExecutionStates.Preparing;

    public ActiveAttempt(AnalystJobMessage job, ClaimGranted claim, DateTimeOffset startedAt)
    {
        Job = job;
        LogicalJobId = claim.LogicalJobId;
        AttemptId = claim.AttemptId;
        AttemptNumber = claim.AttemptNumber;
        FencingToken = claim.FencingToken;
        LeaseDuration = TimeSpan.FromSeconds(claim.LeaseSeconds);
        StaleTimeout = TimeSpan.FromSeconds(claim.StaleTimeoutSeconds);
        ExecutionTimeout = TimeSpan.FromSeconds(claim.ExecutionTimeoutSeconds);
        _leaseExpiresAt = claim.LeaseExpiresAt;
        StartedAt = startedAt;
        CompletionIdempotencyKey = $"completion.{claim.AttemptId}";
    }

    public AnalystJobMessage Job { get; }

    public Guid LogicalJobId { get; }

    public string CorrelationId => Job.Correlation.CorrelationId;

    public string Capability => Job.Capability.Id;

    public Guid AttemptId { get; }

    public int AttemptNumber { get; }

    internal long FencingToken { get; }

    public TimeSpan LeaseDuration { get; }

    public TimeSpan StaleTimeout { get; }

    public TimeSpan ExecutionTimeout { get; }

    public DateTimeOffset StartedAt { get; }

    public string CompletionIdempotencyKey { get; }

    public DateTimeOffset LeaseExpiresAt
    {
        get { lock (_gate) { return _leaseExpiresAt; } }
    }

    public DateTimeOffset? LastConfirmedHeartbeat
    {
        get { lock (_gate) { return _lastConfirmedHeartbeat; } }
    }

    public string ExecutionState
    {
        get { lock (_gate) { return _executionState; } }
        set { lock (_gate) { _executionState = value; } }
    }

    internal void ConfirmHeartbeat(DateTimeOffset confirmedAt, DateTimeOffset leaseExpiresAt)
    {
        lock (_gate)
        {
            _lastConfirmedHeartbeat = confirmedAt;
            _leaseExpiresAt = leaseExpiresAt;
        }
    }
}
