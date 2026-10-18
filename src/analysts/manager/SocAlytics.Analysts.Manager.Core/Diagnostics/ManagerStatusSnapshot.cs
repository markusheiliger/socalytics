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

/// <summary>The queue depth state with its age on the Manager's clock; approximate guidance only.</summary>
public sealed record QueueDepthView(Queue.QueueDepthSnapshot Snapshot, double? AgeSeconds)
{
    public string Wire => Snapshot.State switch
    {
        Queue.QueueDepthStatus.Available => "available",
        Queue.QueueDepthStatus.Stale => "stale",
        Queue.QueueDepthStatus.NoCapability => "no-capability",
        Queue.QueueDepthStatus.Unavailable => "unavailable",
        _ => "not-yet-available",
    };

    public static string FormatAge(double seconds) => seconds switch
    {
        < 60 => $"{Math.Floor(seconds):F0} s",
        < 3600 => $"{Math.Floor(seconds / 60):F0} min",
        _ => $"{Math.Floor(seconds / 3600):F0} h",
    };

    /// <summary>The lines shared by the CLI and the status window.</summary>
    public IReadOnlyList<string> Lines()
    {
        switch (Snapshot.State)
        {
            case Queue.QueueDepthStatus.Available or Queue.QueueDepthStatus.Stale:
                var age = FormatAge(AgeSeconds ?? 0);
                var lines = new List<string>
                {
                    Snapshot.State == Queue.QueueDepthStatus.Stale
                        ? $"Queue (approximate guidance, observed {age} ago, stale):"
                        : $"Queue (approximate guidance, observed {age} ago):",
                };
                lines.AddRange(Snapshot.Capabilities.Select(c => $"  {c.Id}: {c.Waiting}"));
                lines.Add($"  Total: {Snapshot.Total}");
                return lines;
            case Queue.QueueDepthStatus.NoCapability:
                return ["Queue: no capability assigned"];
            case Queue.QueueDepthStatus.Unavailable:
                return [$"Queue: unavailable ({Snapshot.LastError ?? "unknown"})"];
            default:
                return ["Queue: no information yet"];
        }
    }
}

/// <summary>Host CPU and memory wording shared by the CLI and the status window; unreadable values read "not available".</summary>
public static class HostUtilizationText
{
    public static string Cpu(decimal? percent) =>
        percent is { } p ? $"CPU: {p.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} %" : "CPU: not available";

    public static string Memory(long? used, long? total) =>
        used is { } u && total is { } t ? $"Memory: {Bytes(u)} of {Bytes(t)}" : "Memory: not available";

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} GiB",
        >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} MiB",
        _ => $"{bytes} B",
    };

    public static IReadOnlyList<string> Lines(HostUtilizationSample? sample) =>
        [Cpu(sample?.CpuPercent), Memory(sample?.MemoryUsedBytes, sample?.MemoryTotalBytes)];
}

/// <summary>The update notice; the minimum is the value as received, raw when unparsable.</summary>
public sealed record UpdateRequiredStatus(bool Required, string ManagerVersion, string MinimumVersion)
{
    public string Sentence => $"Update required: Manager {ManagerVersion} is below the stamp minimum {MinimumVersion}.";
}

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
    IReadOnlyList<ActiveAttemptStatus> ActiveAttempts,
    QueueDepthView QueueDepth,
    string ManagerVersion,
    HostUtilizationSample? HostUtilization = null,
    string ApplicationProfile = "tray",
    string? Lingering = null,
    UpdateRequiredStatus? UpdateRequired = null);
