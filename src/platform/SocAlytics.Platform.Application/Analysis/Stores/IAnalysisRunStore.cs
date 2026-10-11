using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record AnalysisRunRecord(
    Guid Id,
    Guid TeamId,
    Guid MatchId,
    Guid RecordingSetVersionId,
    string WorkflowId,
    string WorkflowVersion,
    IReadOnlyList<string> RequestedCapabilities,
    string CompletionPolicy,
    AnalysisRunState State,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RunningAt,
    DateTimeOffset? TerminatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record AnalysisRunSnapshotRecord(string SnapshotJson, string SnapshotDigest);

public sealed record WorkflowNodeRecord(
    Guid Id,
    string NodeKey,
    string CapabilityId,
    string CapabilityVersion,
    CapabilityTier Tier,
    ExecutionScope ExecutionScope,
    int Ordinal,
    WorkflowNodeState State,
    Guid? BlockedByNodeId,
    DateTimeOffset UpdatedAt,
    string SnapshotJson);

public sealed record DependencyEdgeRecord(Guid UpstreamNodeId, Guid DependentNodeId, DependencyKind Kind, string? PredicateJson);

public sealed record NewAnalysisRun(
    Guid TeamId,
    Guid MatchId,
    Guid RecordingSetVersionId,
    string WorkflowId,
    string WorkflowVersion,
    IReadOnlyList<string> RequestedCapabilities,
    string CompletionPolicy,
    AnalysisRunState State,
    string? FailureReason,
    AnalysisRunSnapshotRecord Snapshot,
    IReadOnlyList<WorkflowNodeRecord> Nodes,
    IReadOnlyList<DependencyEdgeRecord> Edges);

public sealed record AnalysisRunHierarchy(
    AnalysisRunRecord Run,
    AnalysisRunSnapshotRecord Snapshot,
    IReadOnlyList<WorkflowNodeRecord> Nodes,
    IReadOnlyList<DependencyEdgeRecord> Edges);

public interface IAnalysisRunStore
{
    // Node ids are assigned by the caller (Guid.CreateVersion7) so edges can reference them; the run id is returned.
    Task<Guid> InsertAsync(NewAnalysisRun run, CancellationToken cancellationToken);

    Task<AnalysisRunRecord?> LockRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<bool> TryTransitionAsync(
        Guid runId,
        AnalysisRunState expectedState,
        AnalysisRunState newState,
        string? failureReason,
        CancellationToken cancellationToken);

    // Updates the node only while it is in expectedState.
    Task<bool> TryTransitionNodeAsync(
        Guid nodeId,
        WorkflowNodeState expectedState,
        WorkflowNodeState newState,
        Guid? blockedByNodeId,
        CancellationToken cancellationToken);

    Task<AnalysisRunHierarchy?> LoadAsync(Guid runId, CancellationToken cancellationToken);
}
