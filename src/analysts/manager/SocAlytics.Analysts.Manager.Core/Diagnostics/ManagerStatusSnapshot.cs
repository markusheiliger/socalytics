namespace SocAlytics.Analysts.Manager.Diagnostics;

public sealed record ConnectivityStatus(string State, DateTimeOffset? LastContactAt = null, DateTimeOffset? NextRetryAt = null);

public sealed record DrainStatus(string Purpose, DateTimeOffset Deadline, int ActiveWork);

public sealed record StatusDiagnostic(string Code, string Message, DateTimeOffset ObservedAt, string? Remediation = null);

/// <summary>One claimed attempt; carries no fencing token, credential, or checksum.</summary>
public sealed record ActiveAttemptStatus(
    Guid LogicalJobId,
    int AttemptNumber,
    string Capability,
    string ExecutionState,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastConfirmedHeartbeatAt);

/// <summary>Mirrors local-control.schema.json statusReport; never carries the pairing code or any secret.</summary>
public sealed record ManagerStatusSnapshot(
    string RegistrationState,
    string? PairingPhase,
    string? ManagerId,
    string? StampId,
    string? PlatformEndpoint,
    string? ClubName,
    ConnectivityStatus Connectivity,
    string? OperatingState,
    string? OperatingIntent,
    bool PendingUnregister,
    DrainStatus? Drain,
    double UptimeSeconds,
    object? Preflight,
    IReadOnlyList<StatusDiagnostic> Diagnostics,
    DateTimeOffset? PausedUntil,
    double? PauseRemainingSeconds,
    string? KeyProtection,
    bool? AutostartEnabled,
    string ControlSurface,
    string? DeviceFingerprint,
    Work.ConcurrencySnapshot Concurrency,
    IReadOnlyList<ActiveAttemptStatus> ActiveAttempts);
