using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public enum ManagerOperatingState
{
    Running,
    Paused,
    RuntimeUnavailable,
    Draining,
}

public static class ManagerOperatingStateExtensions
{
    public static string ToApiName(this ManagerOperatingState value) => value switch
    {
        ManagerOperatingState.Running => "running",
        ManagerOperatingState.Paused => "paused",
        ManagerOperatingState.RuntimeUnavailable => "runtime-unavailable",
        ManagerOperatingState.Draining => "draining",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParse(string? name, out ManagerOperatingState value)
    {
        foreach (var candidate in Enum.GetValues<ManagerOperatingState>())
        {
            if (string.Equals(candidate.ToApiName(), name, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed class ManagerStatusReport
{
    public const int MaxReasonLength = 128;
    public const int MaxVersionLength = 64;
    public const int MaxAnalystsLimit = 1024;

    private ManagerStatusReport(
        ManagerOperatingState operatingState, DateTimeOffset? pausedUntil, string? runtimeUnavailableReason,
        int activeAnalysts, int maxAnalysts, string managerVersion)
    {
        OperatingState = operatingState;
        PausedUntil = pausedUntil;
        RuntimeUnavailableReason = runtimeUnavailableReason;
        ActiveAnalysts = activeAnalysts;
        MaxAnalysts = maxAnalysts;
        ManagerVersion = managerVersion;
    }

    public ManagerOperatingState OperatingState { get; }

    public DateTimeOffset? PausedUntil { get; }

    public string? RuntimeUnavailableReason { get; }

    public int ActiveAnalysts { get; }

    public int MaxAnalysts { get; }

    public string ManagerVersion { get; }

    public static bool TryCreate(
        string? operatingState, DateTimeOffset? pausedUntil, string? runtimeUnavailableReason,
        int? activeAnalysts, int? maxAnalysts, string? managerVersion,
        out ManagerStatusReport? report, out IReadOnlyList<FieldViolation> violations)
    {
        var errors = new List<FieldViolation>();
        var stateValid = ManagerOperatingStateExtensions.TryParse(operatingState, out var state);
        if (!stateValid)
        {
            errors.Add(new("operatingState", "Must be running, paused, runtime-unavailable, or draining."));
        }

        if (pausedUntil is not null && !(stateValid && state == ManagerOperatingState.Paused))
        {
            errors.Add(new("pausedUntil", "Allowed only when the operating state is paused."));
        }

        if (runtimeUnavailableReason is not null)
        {
            if (!(stateValid && state == ManagerOperatingState.RuntimeUnavailable))
            {
                errors.Add(new("runtimeUnavailableReason", "Allowed only when the operating state is runtime-unavailable."));
            }
            else if (runtimeUnavailableReason.Length == 0 || runtimeUnavailableReason.Length > MaxReasonLength)
            {
                errors.Add(new("runtimeUnavailableReason", "1 to 128 characters."));
            }
        }

        var countsValid = true;
        if (activeAnalysts is null or < 0)
        {
            errors.Add(new("activeAnalysts", "Required, at least 0."));
            countsValid = false;
        }

        if (maxAnalysts is null or < 0 or > MaxAnalystsLimit)
        {
            errors.Add(new("maxAnalysts", "Required, 0 to 1024."));
            countsValid = false;
        }

        if (countsValid && activeAnalysts > maxAnalysts)
        {
            errors.Add(new("activeAnalysts", "Must not exceed maxAnalysts."));
        }

        if (string.IsNullOrEmpty(managerVersion) || managerVersion.Length > MaxVersionLength)
        {
            errors.Add(new("managerVersion", "Required, at most 64 characters."));
        }

        violations = errors;
        report = errors.Count == 0
            ? new ManagerStatusReport(state, pausedUntil, runtimeUnavailableReason, activeAnalysts!.Value, maxAnalysts!.Value, managerVersion!)
            : null;
        return errors.Count == 0;
    }

    // Rehydration of a stored report; the database constraints enforce the same rules.
    public static ManagerStatusReport Restore(
        ManagerOperatingState operatingState, DateTimeOffset? pausedUntil, string? runtimeUnavailableReason,
        int activeAnalysts, int maxAnalysts, string managerVersion) =>
        new(operatingState, pausedUntil, runtimeUnavailableReason, activeAnalysts, maxAnalysts, managerVersion);
}
