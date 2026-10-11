namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record AnalysisRunSummaryView(
    Guid RunId,
    Guid TeamId,
    Guid MatchId,
    Guid RecordingSetVersionId,
    string WorkflowId,
    string WorkflowVersion,
    string State,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RunningAt,
    DateTimeOffset? TerminatedAt,
    long Version);

public sealed record AnalysisJobCountsView(int Waiting, int Ready, int Claimed, int Completed, int Failed, int Blocked);

public sealed record AnalysisNodeStatusView(
    Guid NodeId,
    string NodeKey,
    string CapabilityId,
    string CapabilityVersion,
    string Tier,
    string ExecutionScope,
    string State,
    string? BlockedByNodeKey,
    AnalysisJobCountsView Jobs);

public sealed record AnalysisRunStatusView(
    AnalysisRunSummaryView Run,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<AnalysisNodeStatusView> Nodes,
    string VersionBasis);

public sealed record AnalysisRunPageKey(DateTimeOffset CreatedAt, Guid Id);

public sealed record AnalysisRunSummaryPageResult(IReadOnlyList<AnalysisRunSummaryView> Items, bool HasMore);

public sealed record AnalysisRunSummaryPage(IReadOnlyList<AnalysisRunSummaryView> Items, string? ContinuationToken);

public interface IAnalysisRunQueries
{
    Task<AnalysisRunStatusView?> GetStatusAsync(Guid runId, CancellationToken cancellationToken);

    Task<AnalysisRunSummaryPageResult> ListForMatchAsync(
        Guid matchId, AnalysisRunPageKey? after, int pageSize, CancellationToken cancellationToken);
}
