namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record AnalysisRunCancelledByView(Guid MemberId, string AccountName);

public sealed record AnalysisRunSupersededByView(Guid RunId, Guid RecordingSetVersionId, DateTimeOffset RecordingSetFinalizedAt);

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
    long Version,
    AnalysisRunCancelledByView? CancelledBy,
    string? CancellationReason,
    bool IsCurrent,
    AnalysisRunSupersededByView? SupersededBy,
    DateTimeOffset? SupersededAt,
    string? CancellationCause);

public sealed record AnalysisJobCountsView(int Waiting, int Ready, int Claimed, int Completed, int Failed, int Blocked, int Cancelled);

public sealed record AnalysisNodeInputCoverageView(
    string UpstreamNodeKey,
    string CoverageMode,
    decimal? MinimumAcceptedFraction,
    int Accepted,
    int Failed,
    int Blocked,
    int Skipped,
    int Missing);

public sealed record AnalysisNodeStatusView(
    Guid NodeId,
    string NodeKey,
    string CapabilityId,
    string CapabilityVersion,
    string Tier,
    string ExecutionScope,
    string State,
    string? BlockedByNodeKey,
    AnalysisJobCountsView Jobs,
    IReadOnlyList<AnalysisNodeInputCoverageView>? InputCoverage = null);

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
