namespace SocAlytics.Platform.Application.Analysis;

public sealed class AnalysisNotificationOptions
{
    public const string SectionName = "Analysis:Notifications";

    public string ConsumerName { get; set; } = "analysis-scheduler-recordings-finalized";

    public int NakDelaySeconds { get; set; } = 10;

    public int ResolveRecheckSeconds { get; set; } = 15;
}
