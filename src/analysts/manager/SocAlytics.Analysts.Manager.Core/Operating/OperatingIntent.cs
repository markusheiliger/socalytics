namespace SocAlytics.Analysts.Manager.Operating;

public enum OperatingIntentKind
{
    Running,
    Paused,
    PausedUntil,
}

public sealed record OperatingIntent(OperatingIntentKind Kind, DateTimeOffset? EndUtc = null, bool PendingUnregister = false)
{
    public static OperatingIntent Running { get; } = new(OperatingIntentKind.Running);

    public static OperatingIntent Paused { get; } = new(OperatingIntentKind.Paused);

    public static OperatingIntent PausedUntil(DateTimeOffset endUtc) => new(OperatingIntentKind.PausedUntil, endUtc.ToUniversalTime());

    public bool IsPaused => Kind != OperatingIntentKind.Running;
}
