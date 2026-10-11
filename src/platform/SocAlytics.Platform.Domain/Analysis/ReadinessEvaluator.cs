namespace SocAlytics.Platform.Domain.Analysis;

public static class SnapshotStatus
{
	public const string Accepted = "accepted";
	public const string Absent = "absent";
	public const string Failed = "failed";
	public const string NotApplicable = "not-applicable";
}

public sealed record InputSnapshotEntry(string UpstreamNodeKey, DependencyKind EdgeKind, string Status, IReadOnlyList<Guid> AcceptedResultIds);

public sealed record ReadyJob(Guid JobId, IReadOnlyList<InputSnapshotEntry>? Snapshot);

public sealed record BlockedJob(Guid JobId, string BlockingNodeKey);

public sealed record NodeTransition(string NodeKey, WorkflowNodeState From, WorkflowNodeState To, string? BlockingNodeKey = null);

public sealed record ReadinessEvaluation(
	IReadOnlyList<ReadyJob> ReadyJobs,
	IReadOnlyList<BlockedJob> BlockedJobs,
	IReadOnlyList<NodeTransition> NodeTransitions,
	AnalysisRunState? RunOutcome,
	RunGraphState ResultState)
{
	public bool HasChanges => ReadyJobs.Count > 0 || BlockedJobs.Count > 0 || NodeTransitions.Count > 0 || RunOutcome is not null;
}

public static class ReadinessEvaluator
{
	public static ReadinessEvaluation Evaluate(RunGraphState state)
	{
		var nodes = state.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
		var jobs = state.Jobs.ToList();
		var requested = state.RequestedCapabilities;
		var ready = new List<ReadyJob>();
		var blockedJobs = new List<BlockedJob>();
		var transitions = new List<NodeTransition>();

		bool changed;
		do
		{
			changed = false;

			foreach (var node in nodes.Values.OrderBy(n => n.NodeKey, StringComparer.Ordinal).ToList())
			{
				if (node.State is not (WorkflowNodeState.Waiting or WorkflowNodeState.Active))
				{
					continue;
				}

				var blocker = BlockingUpstream(node.NodeKey, state, nodes, requested);
				if (blocker is null)
				{
					continue;
				}

				transitions.Add(new NodeTransition(node.NodeKey, node.State, WorkflowNodeState.Blocked, blocker));
				nodes[node.NodeKey] = node with { State = WorkflowNodeState.Blocked };
				changed = true;
			}

			for (var i = 0; i < jobs.Count; i++)
			{
				var job = jobs[i];
				if (job.State is not (LogicalJobState.Waiting or LogicalJobState.Ready))
				{
					continue;
				}

				if (nodes[job.NodeKey].State == WorkflowNodeState.Blocked)
				{
					var blocker = state.Edges
						.Where(e => e.DependentNodeKey == job.NodeKey && IsRequired(e, nodes, requested) && nodes[e.UpstreamNodeKey].State is WorkflowNodeState.Failed or WorkflowNodeState.Blocked)
						.Select(e => e.UpstreamNodeKey)
						.OrderBy(k => k, StringComparer.Ordinal)
						.FirstOrDefault() ?? job.NodeKey;
					blockedJobs.Add(new BlockedJob(job.JobId, blocker));
					jobs[i] = job with { State = LogicalJobState.Blocked };
					changed = true;
					continue;
				}

				if (job.State != LogicalJobState.Waiting || nodes[job.NodeKey].State != WorkflowNodeState.Waiting && nodes[job.NodeKey].State != WorkflowNodeState.Active)
				{
					continue;
				}

				if (!RequiredSatisfied(job, state, nodes, jobs, requested))
				{
					continue;
				}

				var snapshot = job.HasInputSnapshot ? null : BuildSnapshot(job, state, nodes, jobs, requested);
				ready.Add(new ReadyJob(job.JobId, snapshot));
				jobs[i] = job with { State = LogicalJobState.Ready, HasInputSnapshot = true };
				changed = true;
			}

			foreach (var node in nodes.Values.OrderBy(n => n.NodeKey, StringComparer.Ordinal).ToList())
			{
				var own = jobs.Where(j => j.NodeKey == node.NodeKey).ToList();
				var current = node;
				if (current.State == WorkflowNodeState.Waiting && own.Any(j => j.State is LogicalJobState.Ready or LogicalJobState.Claimed))
				{
					transitions.Add(new NodeTransition(current.NodeKey, current.State, WorkflowNodeState.Active));
					current = current with { State = WorkflowNodeState.Active };
					changed = true;
				}

				if (current.State == WorkflowNodeState.Active)
				{
					WorkflowNodeState? next = own.Any(j => j.State == LogicalJobState.Failed) ? WorkflowNodeState.Failed
						: own.Count > 0 && own.All(j => j.State == LogicalJobState.Completed) ? WorkflowNodeState.Completed
						: null;
					if (next is { } target)
					{
						transitions.Add(new NodeTransition(current.NodeKey, current.State, target));
						current = current with { State = target };
						changed = true;
					}
				}

				nodes[node.NodeKey] = current;
			}
		}
		while (changed);

		var resultNodes = state.Nodes.Select(n => nodes[n.NodeKey]).ToList();
		var canProgress = jobs.Any(j => j.State == LogicalJobState.Waiting && nodes[j.NodeKey].State is WorkflowNodeState.Waiting or WorkflowNodeState.Active
			&& Satisfiable(j, state, nodes, jobs, requested));
		var outcome = RunOutcomePolicy.Evaluate(state.RunState, requested, resultNodes, jobs, canProgress);
		var result = state with { RunState = outcome ?? state.RunState, Nodes = resultNodes, Jobs = jobs };
		return new ReadinessEvaluation(ready, blockedJobs, transitions, outcome, result);
	}

	private static bool IsRequired(RunEdgeState edge, Dictionary<string, RunNodeState> nodes, IReadOnlyCollection<string> requested) =>
		edge.Kind switch
		{
			DependencyKind.Required => true,
			DependencyKind.Optional => false,
			_ => ConditionApplies(edge.Predicate, nodes, requested) ?? true,
		};

	// null means indeterminate.
	private static bool? ConditionApplies(ConditionalPredicate? predicate, Dictionary<string, RunNodeState> nodes, IReadOnlyCollection<string> requested)
	{
		switch (predicate?.Kind)
		{
			case ConditionalPredicate.CapabilityRequestedKind:
				return requested.Contains(predicate.CapabilityId!);
			case ConditionalPredicate.NodeAcceptedKind:
				if (!nodes.TryGetValue(predicate.NodeKey!, out var node))
				{
					return false;
				}

				return node.State switch
				{
					WorkflowNodeState.Completed => true,
					WorkflowNodeState.Failed or WorkflowNodeState.Blocked => false,
					_ => null,
				};
			default:
				return null;
		}
	}

	private static string? BlockingUpstream(string nodeKey, RunGraphState state, Dictionary<string, RunNodeState> nodes, IReadOnlyCollection<string> requested) =>
		state.Edges
			.Where(e => e.DependentNodeKey == nodeKey && IsRequired(e, nodes, requested) && nodes[e.UpstreamNodeKey].State is WorkflowNodeState.Failed or WorkflowNodeState.Blocked)
			.Select(e => e.UpstreamNodeKey)
			.OrderBy(k => k, StringComparer.Ordinal)
			.FirstOrDefault();

	private static List<RunJobState> Relevant(RunJobState job, RunEdgeState edge, Dictionary<string, RunNodeState> nodes, List<RunJobState> jobs)
	{
		var upstream = jobs.Where(j => j.NodeKey == edge.UpstreamNodeKey);
		if (nodes[edge.UpstreamNodeKey].Scope == ExecutionScope.Segment && nodes[job.NodeKey].Scope == ExecutionScope.Segment)
		{
			upstream = upstream.Where(j => Equals(j.Segment, job.Segment));
		}

		return upstream.ToList();
	}

	private static bool AllAccepted(List<RunJobState> relevant) =>
		relevant.Count > 0 && relevant.All(j => j.State == LogicalJobState.Completed);

	private static bool RequiredSatisfied(RunJobState job, RunGraphState state, Dictionary<string, RunNodeState> nodes, List<RunJobState> jobs, IReadOnlyCollection<string> requested) =>
		state.Edges
			.Where(e => e.DependentNodeKey == job.NodeKey && IsRequired(e, nodes, requested))
			.All(e => AllAccepted(Relevant(job, e, nodes, jobs)));

	private static bool Satisfiable(RunJobState job, RunGraphState state, Dictionary<string, RunNodeState> nodes, List<RunJobState> jobs, IReadOnlyCollection<string> requested) =>
		state.Edges
			.Where(e => e.DependentNodeKey == job.NodeKey && IsRequired(e, nodes, requested))
			.All(e => nodes[e.UpstreamNodeKey].State is not (WorkflowNodeState.Failed or WorkflowNodeState.Blocked)
				&& (nodes[e.UpstreamNodeKey].State != WorkflowNodeState.Completed || AllAccepted(Relevant(job, e, nodes, jobs))));

	private static List<InputSnapshotEntry> BuildSnapshot(RunJobState job, RunGraphState state, Dictionary<string, RunNodeState> nodes, List<RunJobState> jobs, IReadOnlyCollection<string> requested)
	{
		var entries = new List<InputSnapshotEntry>();
		foreach (var edge in state.Edges.Where(e => e.DependentNodeKey == job.NodeKey).OrderBy(e => e.UpstreamNodeKey, StringComparer.Ordinal))
		{
			if (edge.Kind == DependencyKind.Conditional && ConditionApplies(edge.Predicate, nodes, requested) == false)
			{
				entries.Add(new InputSnapshotEntry(edge.UpstreamNodeKey, edge.Kind, SnapshotStatus.NotApplicable, []));
				continue;
			}

			var relevant = Relevant(job, edge, nodes, jobs);
			if (AllAccepted(relevant))
			{
				var ids = relevant.SelectMany(j => j.AcceptedResultIds).Order().ToList();
				entries.Add(new InputSnapshotEntry(edge.UpstreamNodeKey, edge.Kind, SnapshotStatus.Accepted, ids));
			}
			else
			{
				var failed = nodes[edge.UpstreamNodeKey].State is WorkflowNodeState.Failed or WorkflowNodeState.Blocked;
				entries.Add(new InputSnapshotEntry(edge.UpstreamNodeKey, edge.Kind, failed ? SnapshotStatus.Failed : SnapshotStatus.Absent, []));
			}
		}

		return entries;
	}
}
