using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

public static class AttemptExecutionStates
{
    public const string Preparing = "preparing";
    public const string Running = "running";
    public const string Finalizing = "finalizing";
}

/// <summary>Why the registry stopped an attempt from outside.</summary>
public enum AttemptStopReason
{
    /// <summary>The drain timeout expired under the stop-and-clean-up policy; a failed completion is reported.</summary>
    DrainTimeout,

    /// <summary>The Manager is exiting; nothing is reported.</summary>
    Exit,

    /// <summary>The registration was revoked; nothing is reported.</summary>
    Revoked,
}

/// <summary>In-memory view of one claimed attempt; the fencing token is never logged or persisted.</summary>
public sealed class ActiveAttempt
{
    private readonly object _gate = new();
    private DateTimeOffset _leaseExpiresAt;
    private DateTimeOffset? _lastConfirmedHeartbeat;
    private string _executionState = AttemptExecutionStates.Preparing;
    private AttemptStopReason? _stopReason;

    public ActiveAttempt(AnalystJobMessage job, ClaimGranted claim, DateTimeOffset startedAt, byte[]? jobBytes = null)
    {
        Job = job;
        JobBytes = jobBytes ?? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(job, PlatformJson.Options);
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

    /// <summary>The delivered job message exactly as received; it is written unchanged to <c>job.json</c>.</summary>
    public byte[] JobBytes { get; }

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

    internal AttemptStopReason? StopReason
    {
        get { lock (_gate) { return _stopReason; } }
        set { lock (_gate) { _stopReason = value; } }
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
