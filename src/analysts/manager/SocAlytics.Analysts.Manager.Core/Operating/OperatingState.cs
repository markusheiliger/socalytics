namespace SocAlytics.Analysts.Manager.Operating;

public enum OperatingStateKind
{
    RuntimeUnavailable,
    Running,
    Paused,
    Draining,
    Ended,
}

public enum DrainPurpose
{
    SafeExit,
    Unregister,
    SessionEnd,
}

public sealed record OperatingState(
    OperatingStateKind Kind,
    DrainPurpose? Purpose = null,
    OperatingStateKind? Prior = null,
    DateTimeOffset? Deadline = null);
