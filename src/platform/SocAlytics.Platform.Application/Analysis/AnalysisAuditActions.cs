using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Analysis;

public static class AnalysisAuditActions
{
    public const string RequestAnalysisRunEvent = "analysis.run.request";

    public const string RequestAnalysisRunAction = "request-analysis-run";

    public const string AnalysisRunResource = "analysis-run";

    public const string MatchIdDetail = "matchId";
    public const string RecordingSetVersionIdDetail = "recordingSetVersionId";
    public const string OutcomeDetail = "outcome";

    public static AuditResource AnalysisRun(Guid id) => new(AnalysisRunResource, id.ToString());
}
