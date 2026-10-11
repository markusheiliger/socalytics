namespace SocAlytics.Platform.Infrastructure.Workers;

internal sealed class AnalysisWorkerOptions
{
    public const string SectionName = "Analysis:Workers";

    public bool Enabled { get; set; } = true;
}
