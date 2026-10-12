namespace SocAlytics.Platform.Domain.Analysis;

public sealed record PlannerNode(string NodeKey, ExecutionScope Scope, PinnedJobSnapshot Snapshot);

public sealed record PlannedLogicalJob(
	string NodeKey,
	ExecutionScope Scope,
	AnalysisSegmentReference? Segment,
	PinnedJobSnapshot Snapshot);

public static class LogicalJobPlanner
{
	public static IReadOnlyList<PlannedLogicalJob> Plan(IReadOnlyList<PlannerNode> nodes, IReadOnlyList<AnalysisSegmentReference> segments)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		ArgumentNullException.ThrowIfNull(segments);

		var jobs = new List<PlannedLogicalJob>();
		foreach (var node in nodes)
		{
			if (node.Scope == ExecutionScope.Match)
			{
				jobs.Add(new PlannedLogicalJob(node.NodeKey, node.Scope, null, node.Snapshot));
				continue;
			}

			foreach (var segment in segments)
			{
				jobs.Add(new PlannedLogicalJob(node.NodeKey, node.Scope, segment, node.Snapshot));
			}
		}

		return jobs;
	}
}
