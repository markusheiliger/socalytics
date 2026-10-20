namespace SocAlytics.Platform.Domain.Analysis;

public static class AnalysisStateTransitions
{
	public static bool IsAllowed(AnalysisRunState from, AnalysisRunState to) => (from, to) switch
	{
		(AnalysisRunState.Requested, AnalysisRunState.Running) => true,
		(AnalysisRunState.Running, AnalysisRunState.Completed) => true,
		(AnalysisRunState.Running, AnalysisRunState.PartiallyCompleted) => true,
		(AnalysisRunState.Running, AnalysisRunState.Failed) => true,
		(AnalysisRunState.Requested, AnalysisRunState.Cancelled) => true,
		(AnalysisRunState.Running, AnalysisRunState.Cancelled) => true,
		_ => false,
	};

	public static bool IsAllowed(WorkflowNodeState from, WorkflowNodeState to) => (from, to) switch
	{
		(WorkflowNodeState.Waiting, WorkflowNodeState.Active) => true,
		(WorkflowNodeState.Waiting, WorkflowNodeState.Blocked) => true,
		(WorkflowNodeState.Active, WorkflowNodeState.Completed) => true,
		(WorkflowNodeState.Active, WorkflowNodeState.Blocked) => true,
		(WorkflowNodeState.Active, WorkflowNodeState.Failed) => true,
		(WorkflowNodeState.Waiting, WorkflowNodeState.Cancelled) => true,
		(WorkflowNodeState.Active, WorkflowNodeState.Cancelled) => true,
		_ => false,
	};

	public static bool IsAllowed(LogicalJobState from, LogicalJobState to) => (from, to) switch
	{
		(LogicalJobState.Waiting, LogicalJobState.Ready) => true,
		(LogicalJobState.Waiting, LogicalJobState.Blocked) => true,
		(LogicalJobState.Ready, LogicalJobState.Claimed) => true,
		(LogicalJobState.Ready, LogicalJobState.Blocked) => true,
		(LogicalJobState.Claimed, LogicalJobState.Completed) => true,
		(LogicalJobState.Claimed, LogicalJobState.Ready) => true,
		(LogicalJobState.Claimed, LogicalJobState.Failed) => true,
		(LogicalJobState.Waiting, LogicalJobState.Cancelled) => true,
		(LogicalJobState.Ready, LogicalJobState.Cancelled) => true,
		(LogicalJobState.Claimed, LogicalJobState.Cancelled) => true,
		_ => false,
	};

	// Active to Active is the heartbeat renewal.
	public static bool IsAllowed(ExecutionAttemptState from, ExecutionAttemptState to) => (from, to) switch
	{
		(ExecutionAttemptState.Active, ExecutionAttemptState.Active) => true,
		(ExecutionAttemptState.Active, ExecutionAttemptState.Accepted) => true,
		(ExecutionAttemptState.Active, ExecutionAttemptState.Failed) => true,
		(ExecutionAttemptState.Active, ExecutionAttemptState.Rejected) => true,
		(ExecutionAttemptState.Active, ExecutionAttemptState.Stale) => true,
		(ExecutionAttemptState.Active, ExecutionAttemptState.Cancelled) => true,
		_ => false,
	};
}
