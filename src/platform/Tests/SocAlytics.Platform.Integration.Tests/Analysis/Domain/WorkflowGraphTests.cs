using Shouldly;
using SocAlytics.Platform.Domain.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Domain;

public sealed class WorkflowGraphTests
{
	private static readonly string[] Requested = ["cap"];

	private static WorkflowNodeDefinition Node(string key, ExecutionScope scope = ExecutionScope.Segment) => new(key, "cap-" + key, scope);

	private static WorkflowEdgeDefinition Edge(string up, string down, string kind = "required", ConditionalPredicate? predicate = null) => new(up, down, kind, predicate);

	private static WorkflowGraphValidation Validate(WorkflowNodeDefinition[] nodes, WorkflowEdgeDefinition[] edges, string[]? requested = null) =>
		new WorkflowGraph(nodes, edges).Validate(requested ?? Requested);

	[Fact]
	public void Direct_cycle_is_rejected() =>
		Validate([Node("a"), Node("b")], [Edge("a", "b"), Edge("b", "a")]).Reason.ShouldBe("invalid-graph:cycle");

	[Fact]
	public void Indirect_cycle_is_rejected() =>
		Validate([Node("a"), Node("b"), Node("c")], [Edge("a", "b"), Edge("b", "c"), Edge("c", "a")]).Reason.ShouldBe("invalid-graph:cycle");

	[Fact]
	public void Self_dependency_is_rejected() =>
		Validate([Node("a")], [Edge("a", "a")]).Reason.ShouldBe("invalid-graph:self-dependency");

	[Fact]
	public void Dangling_edge_is_rejected() =>
		Validate([Node("a")], [Edge("a", "missing")]).Reason.ShouldBe("invalid-graph:dangling-edge");

	[Fact]
	public void Undeclared_kind_is_rejected() =>
		Validate([Node("a"), Node("b")], [Edge("a", "b", "mandatory")]).Reason.ShouldBe("invalid-graph:undeclared-kind");

	[Fact]
	public void Empty_capability_set_is_rejected() =>
		Validate([Node("a")], [], []).Reason.ShouldBe("invalid-graph:empty-capability-set");

	[Fact]
	public void Bad_predicates_are_rejected()
	{
		var nodes = new[] { Node("a"), Node("b") };
		Validate(nodes, [Edge("a", "b", "conditional")]).Reason.ShouldBe("invalid-graph:invalid-predicate");
		Validate(nodes, [Edge("a", "b", "conditional", new ConditionalPredicate("unknown"))]).Reason.ShouldBe("invalid-graph:invalid-predicate");
		Validate(nodes, [Edge("a", "b", "conditional", ConditionalPredicate.CapabilityRequested(" "))]).Reason.ShouldBe("invalid-graph:invalid-predicate");
		Validate(nodes, [Edge("a", "b", "required", ConditionalPredicate.NodeAccepted("a"))]).Reason.ShouldBe("invalid-graph:invalid-predicate");
		Validate(nodes, [Edge("a", "b", "conditional", ConditionalPredicate.NodeAccepted("a"))]).IsValid.ShouldBeTrue();
	}

	[Fact]
	public void Predicate_json_is_shape_validated()
	{
		ConditionalPredicate.TryParseJson("{\"kind\":\"capabilityRequested\",\"capabilityId\":\"x\"}", out var p).ShouldBeTrue();
		p.ShouldBe(ConditionalPredicate.CapabilityRequested("x"));
		ConditionalPredicate.TryParseJson("{\"kind\":\"nodeAccepted\",\"capabilityId\":\"x\"}", out _).ShouldBeFalse();
		ConditionalPredicate.TryParseJson("{\"kind\":\"nodeAccepted\",\"nodeKey\":\"n\",\"extra\":\"x\"}", out _).ShouldBeFalse();
		ConditionalPredicate.TryParseJson("not json", out _).ShouldBeFalse();
	}

	[Fact]
	public void Isolated_node_is_valid()
	{
		var result = Validate([Node("a")], []);
		result.IsValid.ShouldBeTrue();
		result.OrderedNodes.Select(n => n.NodeKey).ShouldBe(["a"]);
	}

	[Fact]
	public void Order_is_topological_with_ordinal_tie_break()
	{
		var result = Validate(
			[Node("d"), Node("c"), Node("b"), Node("a", ExecutionScope.Match)],
			[Edge("c", "a"), Edge("d", "b", "optional")]);
		result.IsValid.ShouldBeTrue();
		result.OrderedNodes.Select(n => n.NodeKey).ShouldBe(["c", "a", "d", "b"]);
	}

	[Fact]
	public void Dependency_kind_names_round_trip()
	{
		foreach (var kind in Enum.GetValues<DependencyKind>())
		{
			AnalysisPersistedNames.TryParseDependencyKind(AnalysisPersistedNames.ToPersistedName(kind), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(kind);
		}
	}
}
