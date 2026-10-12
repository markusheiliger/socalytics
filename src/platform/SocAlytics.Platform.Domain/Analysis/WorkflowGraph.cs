namespace SocAlytics.Platform.Domain.Analysis;

public sealed record WorkflowNodeDefinition(string NodeKey, string CapabilityId, ExecutionScope ExecutionScope);

public sealed record WorkflowEdgeDefinition(
	string UpstreamNodeKey,
	string DependentNodeKey,
	string Kind,
	ConditionalPredicate? Predicate = null);

public sealed record WorkflowGraphValidation(
	bool IsValid,
	string? Reason,
	IReadOnlyList<WorkflowNodeDefinition> OrderedNodes)
{
	public static WorkflowGraphValidation Rejected(string reason) => new(false, reason, []);
}

public sealed class WorkflowGraph
{
	public const string EmptyCapabilitySet = "invalid-graph:empty-capability-set";
	public const string DanglingEdge = "invalid-graph:dangling-edge";
	public const string SelfDependency = "invalid-graph:self-dependency";
	public const string Cycle = "invalid-graph:cycle";
	public const string UndeclaredKind = "invalid-graph:undeclared-kind";
	public const string InvalidPredicate = "invalid-graph:invalid-predicate";
	public const string DuplicateNode = "invalid-graph:duplicate-node";

	public WorkflowGraph(IEnumerable<WorkflowNodeDefinition> nodes, IEnumerable<WorkflowEdgeDefinition> edges)
	{
		Nodes = nodes.ToArray();
		Edges = edges.ToArray();
	}

	public IReadOnlyList<WorkflowNodeDefinition> Nodes { get; }

	public IReadOnlyList<WorkflowEdgeDefinition> Edges { get; }

	public WorkflowGraphValidation Validate(IReadOnlyCollection<string> requestedCapabilities)
	{
		if (requestedCapabilities is null || requestedCapabilities.Count == 0)
		{
			return WorkflowGraphValidation.Rejected(EmptyCapabilitySet);
		}

		var byKey = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
		foreach (var node in Nodes)
		{
			if (!byKey.TryAdd(node.NodeKey, node))
			{
				return WorkflowGraphValidation.Rejected(DuplicateNode);
			}
		}

		foreach (var edge in Edges)
		{
			if (!byKey.ContainsKey(edge.UpstreamNodeKey) || !byKey.ContainsKey(edge.DependentNodeKey))
			{
				return WorkflowGraphValidation.Rejected(DanglingEdge);
			}

			if (string.Equals(edge.UpstreamNodeKey, edge.DependentNodeKey, StringComparison.Ordinal))
			{
				return WorkflowGraphValidation.Rejected(SelfDependency);
			}

			if (!AnalysisPersistedNames.TryParseDependencyKind(edge.Kind, out var kind))
			{
				return WorkflowGraphValidation.Rejected(UndeclaredKind);
			}

			var hasValidPredicate = edge.Predicate is { IsValid: true };
			if (kind == DependencyKind.Conditional ? !hasValidPredicate : edge.Predicate is not null)
			{
				return WorkflowGraphValidation.Rejected(InvalidPredicate);
			}
		}

		var inDegree = byKey.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
		var outgoing = byKey.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
		foreach (var edge in Edges.DistinctBy(e => (e.UpstreamNodeKey, e.DependentNodeKey)))
		{
			inDegree[edge.DependentNodeKey]++;
			outgoing[edge.UpstreamNodeKey].Add(edge.DependentNodeKey);
		}

		var ready = new SortedSet<string>(inDegree.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
		var ordered = new List<WorkflowNodeDefinition>(byKey.Count);
		while (ready.Count > 0)
		{
			var key = ready.Min!;
			ready.Remove(key);
			ordered.Add(byKey[key]);
			foreach (var next in outgoing[key])
			{
				if (--inDegree[next] == 0)
				{
					ready.Add(next);
				}
			}
		}

		return ordered.Count != byKey.Count
			? WorkflowGraphValidation.Rejected(Cycle)
			: new WorkflowGraphValidation(true, null, ordered);
	}
}
