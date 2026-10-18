namespace SocAlytics.Platform.Domain.Analysis;

public sealed record RunNodeState(string NodeKey, string CapabilityId, ExecutionScope Scope, WorkflowNodeState State);

public sealed record RunEdgeState(string UpstreamNodeKey, string DependentNodeKey, DependencyKind Kind, ConditionalPredicate? Predicate = null)
{
	public CoveragePolicy CoveragePolicy { get; init; } = CoveragePolicy.Complete;
}

public sealed record RunJobState(
	Guid JobId,
	string NodeKey,
	AnalysisSegmentReference? Segment,
	LogicalJobState State,
	bool HasInputSnapshot,
	IReadOnlyList<Guid> AcceptedResultIds);

public sealed record RunGraphState(
	AnalysisRunState RunState,
	IReadOnlyCollection<string> RequestedCapabilities,
	IReadOnlyList<RunNodeState> Nodes,
	IReadOnlyList<RunEdgeState> Edges,
	IReadOnlyList<RunJobState> Jobs);
