namespace SocAlytics.Platform.Domain.Analysis;

public static class RunOutcomePolicy
{
	// Returns the new run state, or null when the run state does not change.
	public static AnalysisRunState? Evaluate(
		AnalysisRunState current,
		IReadOnlyCollection<string> requestedCapabilities,
		IReadOnlyList<RunNodeState> nodes,
		IReadOnlyList<RunJobState> jobs,
		bool waitingJobCanProgress)
	{
		if (current is not (AnalysisRunState.Requested or AnalysisRunState.Running))
		{
			return null;
		}

		var inFlight = jobs.Any(j => j.State is LogicalJobState.Ready or LogicalJobState.Claimed);
		var terminal = Terminal(requestedCapabilities, nodes, inFlight || waitingJobCanProgress);

		if (current == AnalysisRunState.Requested)
		{
			return inFlight || terminal is not null ? AnalysisRunState.Running : null;
		}

		return terminal;
	}

	private static AnalysisRunState? Terminal(IReadOnlyCollection<string> requested, IReadOnlyList<RunNodeState> nodes, bool canProgress)
	{
		if (nodes.Count > 0 && nodes.All(n => n.State == WorkflowNodeState.Completed))
		{
			return AnalysisRunState.Completed;
		}

		if (canProgress)
		{
			return null;
		}

		var requestedCompleted = nodes.Any(n => requested.Contains(n.CapabilityId) && n.State == WorkflowNodeState.Completed);
		var anyBroken = nodes.Any(n => n.State is WorkflowNodeState.Failed or WorkflowNodeState.Blocked);
		return requestedCompleted && anyBroken ? AnalysisRunState.PartiallyCompleted : AnalysisRunState.Failed;
	}
}
