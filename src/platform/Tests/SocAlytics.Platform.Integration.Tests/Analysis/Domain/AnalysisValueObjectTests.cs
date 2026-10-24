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

	[Theory]
	[InlineData(null, 1, 1)]
	[InlineData(null, 3, 1)]
	[InlineData(null, 4, 1)]
	[InlineData(0.5, 1, 1)]
	[InlineData(0.5, 3, 2)]
	[InlineData(0.5, 4, 2)]
	[InlineData(1.0, 1, 1)]
	[InlineData(1.0, 3, 3)]
	[InlineData(1.0, 4, 4)]
	public void Coverage_policy_computes_the_required_accepted_units(double? fraction, int expected, int required)
	{
		var policy = CoveragePolicy.Partial(fraction is null ? null : (decimal)fraction);
		policy.Mode.ShouldBe(CoverageMode.Partial);
		policy.RequiredAccepted(expected).ShouldBe(required);
	}

	[Fact]
	public void Coverage_policy_defaults_to_complete()
	{
		CoveragePolicy.Complete.Mode.ShouldBe(CoverageMode.Complete);
		CoveragePolicy.Complete.MinimumAcceptedFraction.ShouldBeNull();
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(-0.1)]
	[InlineData(1.01)]
	public void Coverage_policy_rejects_invalid_fractions(double fraction)
	{
		CoveragePolicy.IsValidFraction((decimal)fraction).ShouldBeFalse();
		Should.Throw<ArgumentOutOfRangeException>(() => CoveragePolicy.Partial((decimal)fraction));
	}

	[Fact]
	public void Coverage_policy_accepts_the_upper_bound() => CoveragePolicy.IsValidFraction(1m).ShouldBeTrue();

	private static readonly DateTimeOffset BaseTime = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

	private static Guid IdOf(byte first) => new([first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], bigEndian: true);

	private static RecordingSetVersionKey Version(int minutes, byte id) =>
		new(BaseTime.AddMinutes(minutes), IdOf(id));

	private sealed record TestRun(Guid Id, RecordingSetVersionKey Version, DateTimeOffset CreatedAt)
	{
		public AnalysisRunOrderKey Key => new(Version, CreatedAt, Id);
	}

	private static TestRun Run(byte id, RecordingSetVersionKey version, int createdMinutes) =>
		new(IdOf(id), version, BaseTime.AddMinutes(createdMinutes));

	[Fact]
	public void Version_order_compares_finalization_time_first()
	{
		var earlier = Version(0, 9);
		var later = Version(1, 1);

		RecordingSetVersionOrder.IsOlder(earlier, later).ShouldBeTrue();
		RecordingSetVersionOrder.IsOlder(later, earlier).ShouldBeFalse();
	}

	[Fact]
	public void Version_order_breaks_equal_finalization_times_by_version_id()
	{
		var lower = Version(5, 1);
		var higher = Version(5, 2);

		RecordingSetVersionOrder.IsOlder(lower, higher).ShouldBeTrue();
		RecordingSetVersionOrder.IsOlder(higher, lower).ShouldBeFalse();
	}

	[Fact]
	public void Version_order_follows_the_uuid_byte_order_not_guid_compare()
	{
		var low = new Guid("00000000-0000-0000-0000-000000000001");
		var high = new Guid("01000000-0000-0000-0000-000000000000");
		var time = BaseTime;

		RecordingSetVersionOrder.IsOlder(new(time, low), new(time, high)).ShouldBeTrue();
		RecordingSetVersionOrder.IsOlder(new(time, high), new(time, low)).ShouldBeFalse();
	}

	[Fact]
	public void Equal_versions_are_never_older()
	{
		var version = Version(3, 4);
		var same = new RecordingSetVersionKey(version.FinalizedAt, version.VersionId);

		RecordingSetVersionOrder.IsOlder(version, same).ShouldBeFalse();
		RecordingSetVersionOrder.IsOlder(same, version).ShouldBeFalse();
	}

	[Fact]
	public void Version_order_compares_instants_across_offsets()
	{
		var utc = new RecordingSetVersionKey(BaseTime, IdOf(1));
		var sameInstant = new RecordingSetVersionKey(BaseTime.ToOffset(TimeSpan.FromHours(2)), IdOf(1));

		RecordingSetVersionOrder.IsOlder(utc, sameInstant).ShouldBeFalse();
		RecordingSetVersionOrder.IsOlder(sameInstant, utc).ShouldBeFalse();
	}

	[Fact]
	public void Current_run_is_none_without_runs()
	{
		RecordingSetVersionOrder.SelectCurrent(Array.Empty<TestRun>(), r => r.Key).ShouldBeNull();
		RecordingSetVersionOrder.SelectOlderThanCurrent(Array.Empty<TestRun>(), r => r.Key, r => r.Id).ShouldBeEmpty();
	}

	[Fact]
	public void Current_run_is_the_newest_run_of_the_latest_version()
	{
		var oldVersion = Version(0, 1);
		var newVersion = Version(10, 2);
		var oldRunCreatedLater = Run(1, oldVersion, 100);
		var newRunEarly = Run(2, newVersion, 20);
		var newRunLate = Run(3, newVersion, 30);

		var current = RecordingSetVersionOrder.SelectCurrent(
			[oldRunCreatedLater, newRunEarly, newRunLate], r => r.Key);

		current.ShouldBe(newRunLate);
	}

	[Fact]
	public void Current_run_within_a_version_is_the_newest_created_with_the_id_as_tie_break()
	{
		var version = Version(0, 1);
		var older = Run(1, version, 1);
		var newer = Run(2, version, 2);
		var tieLow = Run(3, version, 5);
		var tieHigh = Run(4, version, 5);

		RecordingSetVersionOrder.SelectCurrent([newer, older], r => r.Key).ShouldBe(newer);
		RecordingSetVersionOrder.SelectCurrent([tieLow, tieHigh], r => r.Key).ShouldBe(tieHigh);
		RecordingSetVersionOrder.SelectCurrent([tieHigh, tieLow], r => r.Key).ShouldBe(tieHigh);
	}

	[Fact]
	public void Runs_older_than_the_current_run_are_those_of_older_versions_only()
	{
		var oldVersion = Version(0, 1);
		var newVersion = Version(10, 2);
		var oldA = Run(8, oldVersion, 1);
		var oldB = Run(5, oldVersion, 2);
		var currentVersionEarly = Run(6, newVersion, 3);
		var current = Run(7, newVersion, 4);

		var older = RecordingSetVersionOrder.SelectOlderThanCurrent(
			[current, oldA, currentVersionEarly, oldB], r => r.Key, r => r.Id);

		older.ShouldBe([oldB, oldA]);
	}

	[Fact]
	public void Newest_first_orders_by_version_then_created_then_id()
	{
		var oldVersion = Version(0, 1);
		var newVersion = Version(10, 2);
		var runs = new[]
		{
			Run(1, oldVersion, 50),
			Run(2, newVersion, 1),
			Run(3, newVersion, 2),
			Run(4, newVersion, 2),
		};

		var ordered = runs.OrderBy(r => r.Key, RecordingSetVersionOrder.NewestFirst).Select(r => r.Id).ToArray();

		ordered.ShouldBe([IdOf(4), IdOf(3), IdOf(2), IdOf(1)]);
	}

	[Fact]
	public void Cancellation_cause_persisted_names()
	{
		AnalysisPersistedNames.ToPersistedName(CancellationCause.Member).ShouldBe("member");
		AnalysisPersistedNames.ToPersistedName(CancellationCause.Superseded).ShouldBe("superseded");
		Should.Throw<ArgumentOutOfRangeException>(() => AnalysisPersistedNames.ToPersistedName((CancellationCause)99));

		AnalysisPersistedNames.TryParseCancellationCause("member", out var member).ShouldBeTrue();
		member.ShouldBe(CancellationCause.Member);
		AnalysisPersistedNames.TryParseCancellationCause("superseded", out var superseded).ShouldBeTrue();
		superseded.ShouldBe(CancellationCause.Superseded);
		AnalysisPersistedNames.TryParseCancellationCause("Member", out _).ShouldBeFalse();
		AnalysisPersistedNames.TryParseCancellationCause(null, out _).ShouldBeFalse();
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
