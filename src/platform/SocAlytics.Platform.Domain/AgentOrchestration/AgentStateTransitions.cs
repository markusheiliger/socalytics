namespace SocAlytics.Platform.Domain.AgentOrchestration;

public static class AgentStateTransitions
{
	// Running to Queued is a retry after a retryable failure, lease expiry, or deadline; Queued or Running to Failed
	// covers exhaustion, a permanent failure, and scope loss.
	public static bool IsAllowed(AgentInvocationState from, AgentInvocationState to) => (from, to) switch
	{
		(AgentInvocationState.Queued, AgentInvocationState.Running) => true,
		(AgentInvocationState.Queued, AgentInvocationState.Failed) => true,
		(AgentInvocationState.Running, AgentInvocationState.Succeeded) => true,
		(AgentInvocationState.Running, AgentInvocationState.Queued) => true,
		(AgentInvocationState.Running, AgentInvocationState.Failed) => true,
		_ => false,
	};

	// Active to Active is the lease renewal; a claim creates the attempt as Claimed and activates it in one step.
	public static bool IsAllowed(AgentAttemptState from, AgentAttemptState to) => (from, to) switch
	{
		(AgentAttemptState.Claimed, AgentAttemptState.Active) => true,
		(AgentAttemptState.Active, AgentAttemptState.Active) => true,
		(AgentAttemptState.Active, AgentAttemptState.Completed) => true,
		(AgentAttemptState.Active, AgentAttemptState.Failed) => true,
		(AgentAttemptState.Active, AgentAttemptState.Expired) => true,
		(AgentAttemptState.Active, AgentAttemptState.Fenced) => true,
		_ => false,
	};

	public static bool IsTerminal(AgentInvocationState state) =>
		state is AgentInvocationState.Succeeded or AgentInvocationState.Failed;
}
