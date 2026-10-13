namespace SocAlytics.Platform.Application.Registry.StatusReporting;

public sealed class StatusReportingOptions
{
    public const string SectionName = "Registry:StatusReporting";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    public int MissedIntervalsBeforeOffline { get; set; } = 3;

    public int IntervalSeconds => (int)Math.Max(1, Math.Round(Interval.TotalSeconds));
}
