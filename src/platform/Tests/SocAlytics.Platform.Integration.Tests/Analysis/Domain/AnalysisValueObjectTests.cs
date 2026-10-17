using Shouldly;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Domain;

public sealed class AnalysisValueObjectTests
{
	private static readonly string Hex = new('a', 64);

	private static Sha256Digest Digest => Sha256Digest.Parse("sha-256:" + Hex);

	[Fact]
	public void Segment_reference_accepts_valid_values()
	{
		var reference = new AnalysisSegmentReference(Guid.NewGuid(), Digest, Digest, 1);
		reference.SegmentNumber.ShouldBe(1);
	}

	[Fact]
	public void Parts_digest_is_not_a_sha256_digest()
	{
		Sha256Digest.TryParse("sha-256-parts:" + Hex, out _).ShouldBeFalse();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Segment_reference_rejects_non_positive_segment_numbers(int number)
	{
		Should.Throw<ArgumentOutOfRangeException>(() => new AnalysisSegmentReference(Guid.NewGuid(), Digest, Digest, number));
	}

	[Theory]
	[InlineData(1L)]
	[InlineData(9007199254740991L)]
	public void Fencing_token_accepts_bounds(long value) => new FencingToken(value).Value.ShouldBe(value);

	[Theory]
	[InlineData(0L)]
	[InlineData(-1L)]
	[InlineData(9007199254740992L)]
	public void Fencing_token_rejects_out_of_range(long value) =>
		Should.Throw<ArgumentOutOfRangeException>(() => new FencingToken(value));

	[Fact]
	public void Budget_exhaustion()
	{
		var budget = new AttemptBudget(3);
		budget.HasRemaining(0).ShouldBeTrue();
		budget.HasRemaining(2).ShouldBeTrue();
		budget.HasRemaining(3).ShouldBeFalse();
		Should.Throw<ArgumentOutOfRangeException>(() => budget.HasRemaining(-1));
		Should.Throw<ArgumentOutOfRangeException>(() => budget.HasRemaining(4));
		Should.Throw<ArgumentOutOfRangeException>(() => new AttemptBudget(0));
	}

	[Fact]
	public void Run_transitions_match_the_state_machine() =>
		AssertTransitions<AnalysisRunState>(AnalysisStateTransitions.IsAllowed,
			(AnalysisRunState.Requested, AnalysisRunState.Running),
			(AnalysisRunState.Running, AnalysisRunState.Completed),
			(AnalysisRunState.Running, AnalysisRunState.PartiallyCompleted),
			(AnalysisRunState.Running, AnalysisRunState.Failed),
			(AnalysisRunState.Requested, AnalysisRunState.Cancelled),
			(AnalysisRunState.Running, AnalysisRunState.Cancelled));

	[Fact]
	public void Node_transitions_match_the_state_machine() =>
		AssertTransitions<WorkflowNodeState>(AnalysisStateTransitions.IsAllowed,
			(WorkflowNodeState.Waiting, WorkflowNodeState.Active),
			(WorkflowNodeState.Waiting, WorkflowNodeState.Blocked),
			(WorkflowNodeState.Active, WorkflowNodeState.Completed),
			(WorkflowNodeState.Active, WorkflowNodeState.Blocked),
			(WorkflowNodeState.Active, WorkflowNodeState.Failed),
			(WorkflowNodeState.Waiting, WorkflowNodeState.Cancelled),
			(WorkflowNodeState.Active, WorkflowNodeState.Cancelled));

	[Fact]
	public void Job_transitions_match_the_state_machine() =>
		AssertTransitions<LogicalJobState>(AnalysisStateTransitions.IsAllowed,
			(LogicalJobState.Waiting, LogicalJobState.Ready),
			(LogicalJobState.Waiting, LogicalJobState.Blocked),
			(LogicalJobState.Ready, LogicalJobState.Claimed),
			(LogicalJobState.Ready, LogicalJobState.Blocked),
			(LogicalJobState.Claimed, LogicalJobState.Completed),
			(LogicalJobState.Claimed, LogicalJobState.Ready),
			(LogicalJobState.Claimed, LogicalJobState.Failed),
			(LogicalJobState.Waiting, LogicalJobState.Cancelled),
			(LogicalJobState.Ready, LogicalJobState.Cancelled),
			(LogicalJobState.Claimed, LogicalJobState.Cancelled));

	[Fact]
	public void Attempt_transitions_match_the_state_machine() =>
		AssertTransitions<ExecutionAttemptState>(AnalysisStateTransitions.IsAllowed,
			(ExecutionAttemptState.Active, ExecutionAttemptState.Active),
			(ExecutionAttemptState.Active, ExecutionAttemptState.Accepted),
			(ExecutionAttemptState.Active, ExecutionAttemptState.Failed),
			(ExecutionAttemptState.Active, ExecutionAttemptState.Rejected),
			(ExecutionAttemptState.Active, ExecutionAttemptState.Stale),
			(ExecutionAttemptState.Active, ExecutionAttemptState.Cancelled));

	[Fact]
	public void Nothing_leaves_cancelled()
	{
		foreach (var to in Enum.GetValues<AnalysisRunState>())
		{
			AnalysisStateTransitions.IsAllowed(AnalysisRunState.Cancelled, to).ShouldBeFalse();
		}

		foreach (var to in Enum.GetValues<WorkflowNodeState>())
		{
			AnalysisStateTransitions.IsAllowed(WorkflowNodeState.Cancelled, to).ShouldBeFalse();
		}

		foreach (var to in Enum.GetValues<LogicalJobState>())
		{
			AnalysisStateTransitions.IsAllowed(LogicalJobState.Cancelled, to).ShouldBeFalse();
		}

		foreach (var to in Enum.GetValues<ExecutionAttemptState>())
		{
			AnalysisStateTransitions.IsAllowed(ExecutionAttemptState.Cancelled, to).ShouldBeFalse();
		}
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("   \t ", null)]
	[InlineData("  wrong recording  ", "wrong recording")]
	[InlineData("line one\nline two", "line one\nline two")]
	[InlineData("line one\r\nline two", "line one\r\nline two")]
	public void Cancellation_reason_is_trimmed_and_empty_becomes_null(string? reason, string? expected)
	{
		RunCancellation.TryNormalizeReason(reason, out var normalized, out var violation).ShouldBeTrue();
		normalized.ShouldBe(expected);
		violation.ShouldBeNull();
	}

	[Fact]
	public void Cancellation_reason_length_limit_is_500_characters()
	{
		RunCancellation.TryNormalizeReason(new string('x', 500), out var normalized, out _).ShouldBeTrue();
		normalized!.Length.ShouldBe(500);
		RunCancellation.TryNormalizeReason("  " + new string('x', 500) + "  ", out normalized, out _).ShouldBeTrue();
		normalized!.Length.ShouldBe(500);

		RunCancellation.TryNormalizeReason(new string('x', 501), out normalized, out var violation).ShouldBeFalse();
		normalized.ShouldBeNull();
		violation.ShouldNotBeNull();
		violation.Field.ShouldBe("reason");
	}

	[Theory]
	[InlineData("a\tb")]
	[InlineData("a\rb")]
	[InlineData("a\0b")]
	[InlineData("a\u001Bb")]
	[InlineData("a\u0085b")]
	public void Cancellation_reason_refuses_control_characters_other_than_line_breaks(string reason)
	{
		RunCancellation.TryNormalizeReason(reason, out var normalized, out var violation).ShouldBeFalse();
		normalized.ShouldBeNull();
		violation.ShouldNotBeNull();
		violation.Field.ShouldBe("reason");
	}

	[Theory]
	[InlineData(5L, 5L, true)]
	[InlineData(5L, 4L, true)]
	[InlineData(5L, 3L, false)]
	[InlineData(5L, 6L, false)]
	[InlineData(1L, 0L, true)]
	public void Cancellation_repeat_is_idempotent_for_the_current_or_previous_version(long current, long ifMatch, bool expected) =>
		RunCancellation.IsIdempotentRepeatVersion(current, ifMatch).ShouldBe(expected);

	[Fact]
	public void Scope_and_tier_names_round_trip()
	{
		foreach (var scope in Enum.GetValues<ExecutionScope>())
		{
			AnalysisPersistedNames.TryParseExecutionScope(AnalysisPersistedNames.ToPersistedName(scope), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(scope);
		}

		foreach (var tier in Enum.GetValues<CapabilityTier>())
		{
			AnalysisPersistedNames.TryParseCapabilityTier(AnalysisPersistedNames.ToPersistedName(tier), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(tier);
		}

		AnalysisPersistedNames.ToPersistedName(ExecutionScope.Segment).ShouldBe("segment");
		AnalysisPersistedNames.ToPersistedName(ExecutionScope.Match).ShouldBe("match");
		AnalysisPersistedNames.ToPersistedName(CapabilityTier.LowLevel).ShouldBe("low-level");
		AnalysisPersistedNames.ToPersistedName(CapabilityTier.HighLevel).ShouldBe("high-level");
		AnalysisPersistedNames.TryParseExecutionScope("Segment", out _).ShouldBeFalse();
		AnalysisPersistedNames.TryParseCapabilityTier("LowLevel", out _).ShouldBeFalse();
	}

	[Fact]
	public void State_enum_persisted_names_are_pascal_case_member_names()
	{
		AnalysisRunState.PartiallyCompleted.ToString().ShouldBe("PartiallyCompleted");
		LogicalJobState.Claimed.ToString().ShouldBe("Claimed");
		WorkflowNodeState.Blocked.ToString().ShouldBe("Blocked");
		ExecutionAttemptState.Accepted.ToString().ShouldBe("Accepted");
		foreach (var state in Enum.GetValues<AnalysisRunState>())
		{
			Enum.Parse<AnalysisRunState>(state.ToString()).ShouldBe(state);
		}
	}

	private static void AssertTransitions<T>(Func<T, T, bool> isAllowed, params (T From, T To)[] allowed)
		where T : struct, Enum
	{
		var set = allowed.ToHashSet();
		foreach (var from in Enum.GetValues<T>())
		{
			foreach (var to in Enum.GetValues<T>())
			{
				isAllowed(from, to).ShouldBe(set.Contains((from, to)), $"{from} -> {to}");
			}
		}
	}
}
