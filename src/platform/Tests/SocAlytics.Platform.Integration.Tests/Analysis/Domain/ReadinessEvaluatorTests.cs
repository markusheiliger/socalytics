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

	private static RunNodeState Seg(string key, WorkflowNodeState s = WorkflowNodeState.Active) => N(key, s, ExecutionScope.Segment);

	private static RunJobState SegJob(string node, int number, LogicalJobState s, bool snapshot = false) =>
		new(Guid.NewGuid(), node, new AnalysisSegmentReference(Guid.Empty, Sha("1"), Sha("2"), number), s, snapshot, s == LogicalJobState.Completed ? [Result] : []);

	private static RunJobState[] SegJobs(string node, params LogicalJobState[] states) =>
		states.Select((s, i) => SegJob(node, i + 1, s)).ToArray();

	private static RunJobState[] Plus(RunJobState[] jobs, params RunJobState[] more) => [.. jobs, .. more];

	private static RunEdgeState Partial(string up, string down, decimal? fraction = null, DependencyKind kind = DependencyKind.Required, ConditionalPredicate? p = null) =>
		new(up, down, kind, p) { CoveragePolicy = CoveragePolicy.Partial(fraction) };

	private static string SegId(int number) => $"{Guid.Empty}-{number}";

	private static InputSnapshotEntry SnapshotOf(ReadinessEvaluation r, string upstream) =>
		r.ReadyJobs.Single(j => j.Snapshot is not null).Snapshot!.Single(e => e.UpstreamNodeKey == upstream);

	[Theory]
	[InlineData(LogicalJobState.Waiting)]
	[InlineData(LogicalJobState.Ready)]
	[InlineData(LogicalJobState.Claimed)]
	public void Partial_barrier_waits_for_every_segment_job_even_with_the_minimum_met(LogicalJobState pending)
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a"), N("b")], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, pending), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Waiting);
		NodeState(r, "b").ShouldBe(WorkflowNodeState.Waiting);
		r.BlockedJobs.ShouldBeEmpty();
	}

	[Fact]
	public void Partial_barrier_is_ready_with_a_failed_segment_and_snapshots_the_coverage()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b", 0.5m)],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		NodeState(r, "b").ShouldBe(WorkflowNodeState.Active);
		var entry = SnapshotOf(r, "a");
		entry.Status.ShouldBe(SnapshotStatus.Partial);
		entry.CoverageMode.ShouldBe(CoverageMode.Partial);
		entry.MinimumAcceptedFraction.ShouldBe(0.5m);
		entry.AcceptedResultIds.ShouldBe([Result]);
		entry.Coverage.ShouldNotBeNull();
		entry.Coverage!.Select(c => (c.SegmentId, c.State)).ShouldBe(
			[(SegId(1), SegmentCoverageState.Accepted), (SegId(2), SegmentCoverageState.Failed)]);
	}

	[Fact]
	public void Partial_barrier_with_all_segments_accepted_snapshots_accepted()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Completed), N("b")], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Completed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		var entry = SnapshotOf(r, "a");
		entry.Status.ShouldBe(SnapshotStatus.Accepted);
		entry.CoverageMode.ShouldBe(CoverageMode.Partial);
		entry.Coverage!.Select(c => c.State).ShouldAllBe(s => s == SegmentCoverageState.Accepted);
	}

	[Fact]
	public void Partial_barrier_with_all_segments_accepted_completes_the_run()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Completed), N("b", WorkflowNodeState.Active)], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Completed), J("b", LogicalJobState.Completed, snapshot: true))));

		NodeState(r, "b").ShouldBe(WorkflowNodeState.Completed);
		r.RunOutcome.ShouldBe(AnalysisRunState.Completed);
	}

	[Theory]
	[InlineData(1, 3, 0.5, false)]
	[InlineData(2, 1, 0.5, true)]
	[InlineData(1, 2, 0.34, false)]
	[InlineData(1, 2, 0.33, true)]
	[InlineData(2, 2, 0.75, false)]
	[InlineData(3, 1, 0.75, true)]
	[InlineData(1, 3, null, true)]
	[InlineData(0, 3, null, false)]
	public void Partial_barrier_rounds_the_required_count_up(int accepted, int failed, double? fraction, bool ready)
	{
		var states = Enumerable.Repeat(LogicalJobState.Completed, accepted).Concat(Enumerable.Repeat(LogicalJobState.Failed, failed)).ToArray();
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b", (decimal?)fraction)],
			Plus(SegJobs("a", states), J("b"))));

		JobState(r, "b").ShouldBe(ready ? LogicalJobState.Ready : LogicalJobState.Blocked);
		NodeState(r, "b").ShouldBe(ready ? WorkflowNodeState.Active : WorkflowNodeState.Blocked);
	}

	[Fact]
	public void Partial_barrier_below_the_minimum_blocks_the_dependent_and_names_the_upstream()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b", 0.5m)],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed, LogicalJobState.Failed, LogicalJobState.Failed), J("b"))));

		var blocked = r.BlockedJobs.Single();
		blocked.BlockingNodeKey.ShouldBe("a");
		blocked.BelowMinimumCoverage.ShouldBeTrue();
		r.NodeTransitions.ShouldContain(t => t.NodeKey == "b" && t.To == WorkflowNodeState.Blocked && t.BlockingNodeKey == "a");
		r.ReadyJobs.ShouldBeEmpty();
	}

	[Fact]
	public void Partial_barrier_without_an_accepted_segment_blocks_the_dependent()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Failed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Blocked);
		r.BlockedJobs.Single().BelowMinimumCoverage.ShouldBeTrue();
	}

	[Fact]
	public void Partial_barrier_blocks_when_the_upstream_node_is_blocked_by_its_own_failed_upstream()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("x", WorkflowNodeState.Failed), Seg("a", WorkflowNodeState.Waiting), N("b")],
			[E("x", "a"), Partial("a", "b")],
			Plus(Plus(SegJobs("x", LogicalJobState.Failed, LogicalJobState.Failed), SegJobs("a", LogicalJobState.Waiting, LogicalJobState.Waiting)), J("b"))));

		NodeState(r, "a").ShouldBe(WorkflowNodeState.Blocked);
		r.ResultState.Jobs.Where(j => j.NodeKey == "a").ShouldAllBe(j => j.State == LogicalJobState.Blocked);
		NodeState(r, "b").ShouldBe(WorkflowNodeState.Blocked);
		JobState(r, "b").ShouldBe(LogicalJobState.Blocked);
		r.BlockedJobs.Single(j => j.JobId == r.ResultState.Jobs.Single(x => x.NodeKey == "b").JobId).BlockingNodeKey.ShouldBe("a");
	}

	[Theory]
	[InlineData(LogicalJobState.Failed, LogicalJobState.Blocked, "c")]
	[InlineData(LogicalJobState.Claimed, LogicalJobState.Waiting, null)]
	public void A_complete_edge_still_blocks_or_holds_next_to_a_met_partial_barrier(LogicalJobState completeUpstream, LogicalJobState expected, string? blocker)
	{
		var cNode = completeUpstream == LogicalJobState.Failed ? WorkflowNodeState.Failed : WorkflowNodeState.Active;
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("c", cNode), N("b")], [Partial("a", "b"), E("c", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), J("c", completeUpstream), J("b"))));

		JobState(r, "b").ShouldBe(expected);
		if (blocker is not null)
		{
			var blocked = r.BlockedJobs.Single();
			blocked.BlockingNodeKey.ShouldBe(blocker);
			blocked.BelowMinimumCoverage.ShouldBeFalse();
		}
	}

	[Fact]
	public void Conditional_partial_edge_that_applies_is_a_partial_barrier()
	{
		var p = ConditionalPredicate.CapabilityRequested("cap-x");
		var r = ReadinessEvaluator.Evaluate(G(["cap-x", "cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")],
			[Partial("a", "b", kind: DependencyKind.Conditional, p: p)],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		var entry = SnapshotOf(r, "a");
		entry.Status.ShouldBe(SnapshotStatus.Partial);
		entry.EdgeKind.ShouldBe(DependencyKind.Conditional);
		entry.Coverage!.Count.ShouldBe(2);
	}

	[Fact]
	public void Conditional_partial_edge_that_applies_blocks_below_the_minimum()
	{
		var p = ConditionalPredicate.CapabilityRequested("cap-x");
		var r = ReadinessEvaluator.Evaluate(G(["cap-x", "cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")],
			[Partial("a", "b", kind: DependencyKind.Conditional, p: p)],
			Plus(SegJobs("a", LogicalJobState.Failed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Blocked);
		r.BlockedJobs.Single().BelowMinimumCoverage.ShouldBeTrue();
	}

	[Fact]
	public void Conditional_partial_edge_that_is_not_applicable_neither_blocks_nor_carries_coverage()
	{
		var p = ConditionalPredicate.CapabilityRequested("cap-x");
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")],
			[Partial("a", "b", kind: DependencyKind.Conditional, p: p)],
			Plus(SegJobs("a", LogicalJobState.Failed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Ready);
		var entry = SnapshotOf(r, "a");
		entry.Status.ShouldBe(SnapshotStatus.NotApplicable);
		entry.CoverageMode.ShouldBe(CoverageMode.Complete);
		entry.Coverage.ShouldBeNull();
	}

	[Fact]
	public void Partial_policy_on_a_match_scoped_upstream_behaves_as_complete()
	{
		var failed = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b")],
			[J("a", LogicalJobState.Failed), J("b")]));
		JobState(failed, "b").ShouldBe(LogicalJobState.Blocked);
		failed.BlockedJobs.Single().BelowMinimumCoverage.ShouldBeFalse();

		var claimed = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Active), N("b")], [Partial("a", "b")],
			[J("a", LogicalJobState.Claimed), J("b")]));
		JobState(claimed, "b").ShouldBe(LogicalJobState.Waiting);

		var accepted = ReadinessEvaluator.Evaluate(G(["cap-b"], [N("a", WorkflowNodeState.Completed), N("b")], [Partial("a", "b")],
			[J("a", LogicalJobState.Completed), J("b")]));
		JobState(accepted, "b").ShouldBe(LogicalJobState.Ready);
		var entry = SnapshotOf(accepted, "a");
		entry.Status.ShouldBe(SnapshotStatus.Accepted);
		entry.CoverageMode.ShouldBe(CoverageMode.Complete);
		entry.Coverage.ShouldBeNull();
	}

	[Fact]
	public void Segment_to_segment_partial_edge_keeps_node_level_blocking()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), Seg("b", WorkflowNodeState.Waiting)], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), SegJobs("b", LogicalJobState.Waiting, LogicalJobState.Waiting))));

		NodeState(r, "b").ShouldBe(WorkflowNodeState.Blocked);
		r.ResultState.Jobs.Where(j => j.NodeKey == "b").ShouldAllBe(j => j.State == LogicalJobState.Blocked);
		r.BlockedJobs.ShouldAllBe(j => !j.BelowMinimumCoverage && j.BlockingNodeKey == "a");
	}

	[Fact]
	public void Run_is_partially_completed_when_a_bridged_segment_failed()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-a", "cap-b"], [Seg("a"), N("b", WorkflowNodeState.Active)], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), J("b", LogicalJobState.Completed, snapshot: true))));

		NodeState(r, "a").ShouldBe(WorkflowNodeState.Failed);
		NodeState(r, "b").ShouldBe(WorkflowNodeState.Completed);
		r.RunOutcome.ShouldBe(AnalysisRunState.PartiallyCompleted);
	}

	[Fact]
	public void Run_fails_when_nothing_requested_completed_below_the_minimum()
	{
		var r = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a"), N("b")], [Partial("a", "b", 0.5m)],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed, LogicalJobState.Failed), J("b"))));

		JobState(r, "b").ShouldBe(LogicalJobState.Blocked);
		r.RunOutcome.ShouldBe(AnalysisRunState.Failed);
	}

	[Fact]
	public void Partial_barrier_evaluation_is_idempotent()
	{
		var first = ReadinessEvaluator.Evaluate(G(["cap-b"], [Seg("a", WorkflowNodeState.Failed), N("b")], [Partial("a", "b")],
			Plus(SegJobs("a", LogicalJobState.Completed, LogicalJobState.Failed), J("b"))));
		first.HasChanges.ShouldBeTrue();
		ReadinessEvaluator.Evaluate(first.ResultState).HasChanges.ShouldBeFalse();
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
