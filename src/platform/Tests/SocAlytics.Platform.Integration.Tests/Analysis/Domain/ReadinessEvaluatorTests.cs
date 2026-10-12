using Shouldly;
using SocAlytics.Platform.Domain.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Domain;

public sealed class ReadinessEvaluatorTests
{
	private static readonly Guid Result = Guid.NewGuid();

	private static RunNodeState N(string key, WorkflowNodeState s = WorkflowNodeState.Waiting, ExecutionScope scope = ExecutionScope.Match) => new(key, "cap-" + key, scope, s);

	private static RunEdgeState E(string up, string down, DependencyKind kind = DependencyKind.Required, ConditionalPredicate? p = null) => new(up, down, kind, p);

	private static RunJobState J(string node, LogicalJobState s = LogicalJobState.Waiting, bool snapshot = false) =>
		new(Guid.NewGuid(), node, null, s, snapshot, s == LogicalJobState.Completed ? [Result] : []);

	private static RunGraphState G(string[] requested, RunNodeState[] nodes, RunEdgeState[] edges, RunJobState[] jobs, AnalysisRunState run = AnalysisRunState.Running) =>
		new(run, requested, nodes, edges, jobs);

	private static LogicalJobState JobState(ReadinessEvaluation r, string node) => r.ResultState.Jobs.Single(j => j.NodeKey == node).State;

	private static WorkflowNodeState NodeState(ReadinessEvaluation r, string node) => r.ResultState.Nodes.Single(n => n.NodeKey == node).State;

	[Theory]
	[InlineData(DependencyKind.Required, LogicalJobState.Completed, LogicalJobState.Ready)]
	[InlineData(DependencyKind.Required, LogicalJobState.Claimed, LogicalJobState.Waiting)]
	[InlineData(DependencyKind.Optional, LogicalJobState.Waiting, LogicalJobState.Ready)]
	[InlineData(DependencyKind.Optional, LogicalJobState.Claimed, LogicalJobState.Ready)]
	public void Kinds_gate_readiness(DependencyKind kind, LogicalJobState upstream, LogicalJobState expected)
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Active), N("b")], [E("a", "b", kind)], [J("a", upstream), J("b")]));
		JobState(r, "b").ShouldBe(expected);
	}

	[Fact]
	public void Optional_failed_upstream_does_not_block_and_is_snapshotted_failed()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Failed), N("b")], [E("a", "b", DependencyKind.Optional)], [J("a", LogicalJobState.Failed), J("b")]));
		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		r.ReadyJobs.Single().Snapshot!.Single().Status.ShouldBe("failed");
	}

	[Fact]
	public void Optional_absent_upstream_is_snapshotted_absent()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Active), N("b")], [E("a", "b", DependencyKind.Optional)], [J("a", LogicalJobState.Claimed), J("b")]));
		r.ReadyJobs.Single().Snapshot!.Single().Status.ShouldBe("absent");
	}

	[Fact]
	public void Accepted_snapshot_carries_result_ids()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Completed), N("b")], [E("a", "b")], [J("a", LogicalJobState.Completed), J("b")]));
		var entry = r.ReadyJobs.Single().Snapshot!.Single();
		entry.Status.ShouldBe("accepted");
		entry.AcceptedResultIds.ShouldBe([Result]);
	}

	[Fact]
	public void Existing_snapshot_is_not_retaken()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Completed), N("b")], [E("a", "b", DependencyKind.Optional)], [J("a", LogicalJobState.Completed), J("b", snapshot: true)]));
		r.ReadyJobs.Single().Snapshot.ShouldBeNull();
	}

	[Theory]
	[InlineData(true, WorkflowNodeState.Waiting, LogicalJobState.Waiting)]
	[InlineData(false, WorkflowNodeState.Waiting, LogicalJobState.Ready)]
	[InlineData(false, WorkflowNodeState.Failed, LogicalJobState.Ready)]
	public void Capability_requested_conditional(bool requested, WorkflowNodeState upstreamNode, LogicalJobState expected)
	{
		var upJob = upstreamNode == WorkflowNodeState.Failed ? LogicalJobState.Failed : LogicalJobState.Waiting;
		var p = ConditionalPredicate.CapabilityRequested("cap-x");
		var r = ReadinessEvaluator.Evaluate(G(requested ? ["cap-x", "cap-b"] : ["cap-b"], [N("a", upstreamNode), N("b")], [E("a", "b", DependencyKind.Conditional, p)], [J("a", upJob), J("b")]));
		JobState(r, "b").ShouldBe(expected);
	}

	[Theory]
	[InlineData(WorkflowNodeState.Completed, LogicalJobState.Ready)]
	[InlineData(WorkflowNodeState.Active, LogicalJobState.Waiting)]
	public void Node_accepted_conditional(WorkflowNodeState predicateNode, LogicalJobState expected)
	{
		var pj = predicateNode == WorkflowNodeState.Completed ? LogicalJobState.Completed : LogicalJobState.Claimed;
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("p", predicateNode), N("a", WorkflowNodeState.Active), N("b")],
			[E("a", "b", DependencyKind.Conditional, ConditionalPredicate.NodeAccepted("p"))],
			[J("p", pj), J("a", predicateNode == WorkflowNodeState.Completed ? LogicalJobState.Completed : LogicalJobState.Claimed), J("b")]));
		JobState(r, "b").ShouldBe(expected);
	}

	[Fact]
	public void Node_accepted_determinate_false_makes_edge_optional()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("p", WorkflowNodeState.Failed), N("a", WorkflowNodeState.Active), N("b")],
			[E("a", "b", DependencyKind.Conditional, ConditionalPredicate.NodeAccepted("p"))],
			[J("p", LogicalJobState.Failed), J("a", LogicalJobState.Claimed), J("b")]));
		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		r.ReadyJobs.Single().Snapshot!.Single().Status.ShouldBe("not-applicable");
	}

	[Theory]
	[InlineData(false, LogicalJobState.Waiting)]
	[InlineData(true, LogicalJobState.Ready)]
	public void Segment_barrier(bool complete, LogicalJobState expected)
	{
		RunJobState Seg(int n, LogicalJobState s) => new(Guid.NewGuid(), "a", new AnalysisSegmentReference(Guid.Empty, Sha("1"), Sha("2"), n), s, false, s == LogicalJobState.Completed ? [Result] : []);
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Active, ExecutionScope.Segment), N("b")], [E("a", "b")],
			[Seg(1, LogicalJobState.Completed), Seg(2, complete ? LogicalJobState.Completed : LogicalJobState.Claimed), J("b")]));
		JobState(r, "b").ShouldBe(expected);
	}

	private static SocAlytics.Platform.Domain.Recordings.Sha256Digest Sha(string c) =>
		SocAlytics.Platform.Domain.Recordings.Sha256Digest.FromBytes(System.Security.Cryptography.SHA256.HashData([(byte)c[0]]));

	[Fact]
	public void Failure_propagates_while_independent_branch_continues()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b", "cap-c"], [N("a", WorkflowNodeState.Failed), N("b"), N("c", WorkflowNodeState.Completed), N("d")],
			[E("a", "b"), E("c", "d")], [J("a", LogicalJobState.Failed), J("b"), J("c", LogicalJobState.Completed), J("d")]));
		NodeState(r, "b").ShouldBe(WorkflowNodeState.Blocked);
		JobState(r, "b").ShouldBe(LogicalJobState.Blocked);
		r.BlockedJobs.Single().BlockingNodeKey.ShouldBe("a");
		JobState(r, "d").ShouldBe(LogicalJobState.Ready);
		r.RunOutcome.ShouldBeNull();
	}

	[Fact]
	public void Claimed_job_keeps_attempt_when_node_blocks()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Failed), N("b", WorkflowNodeState.Active)], [E("a", "b")], [J("a", LogicalJobState.Failed), J("b", LogicalJobState.Claimed)]));
		JobState(r, "b").ShouldBe(LogicalJobState.Claimed);
	}

	[Fact]
	public void Outcome_completed()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-a"], [N("a", WorkflowNodeState.Active)], [], [J("a", LogicalJobState.Completed)]));
		r.RunOutcome.ShouldBe(AnalysisRunState.Completed);
		NodeState(r, "a").ShouldBe(WorkflowNodeState.Completed);
	}

	[Fact]
	public void Outcome_partially_completed()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-a", "cap-b"], [N("a", WorkflowNodeState.Completed), N("b", WorkflowNodeState.Active)], [], [J("a", LogicalJobState.Completed), J("b", LogicalJobState.Failed)]));
		r.RunOutcome.ShouldBe(AnalysisRunState.PartiallyCompleted);
	}

	[Fact]
	public void Outcome_failed()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-a", "cap-b"], [N("a", WorkflowNodeState.Active), N("b", WorkflowNodeState.Blocked)], [E("a", "b")], [J("a", LogicalJobState.Failed), J("b", LogicalJobState.Blocked)]));
		r.RunOutcome.ShouldBe(AnalysisRunState.Failed);
	}

	[Fact]
	public void Requested_becomes_running_when_first_job_ready()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-a"], [N("a")], [], [J("a")], AnalysisRunState.Requested));
		r.RunOutcome.ShouldBe(AnalysisRunState.Running);
		NodeState(r, "a").ShouldBe(WorkflowNodeState.Active);
	}

	[Fact]
	public void Evaluation_is_idempotent()
	{
		var first = ReadinessEvaluator.Evaluate(G(["cap-b", "cap-c"], [N("a", WorkflowNodeState.Failed), N("b"), N("c"), N("d")],
			[E("a", "b"), E("c", "d", DependencyKind.Optional)], [J("a", LogicalJobState.Failed), J("b"), J("c"), J("d")], AnalysisRunState.Requested));
		first.HasChanges.ShouldBeTrue();
		ReadinessEvaluator.Evaluate(first.ResultState).HasChanges.ShouldBeFalse();
	}
}
