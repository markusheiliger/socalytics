using Shouldly;
using SocAlytics.Platform.Domain.Analysis.Evidence;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class EvidenceCoveragePlannerTests
{
	private const int D = 10;

	private static EvidencePlanMember Member(params TimelineSpan[] spans) =>
		new(Guid.NewGuid(), "sha256:" + new string('a', 64), spans);

	private static TimelineSpan Span(long matchStart, long length, long mediaStart = 0) =>
		new(mediaStart, mediaStart + length, matchStart);

	private static (long Start, long End, int N, EvidencePlanMember? Member)[] Shape(
		IReadOnlyList<EvidencePlannedPart> parts) =>
		parts.Select(p => (p.StartMs, p.EndMs, p.SegmentNumber, p.Member)).ToArray();

	private static void ShouldBeContiguous(IReadOnlyList<EvidencePlannedPart> parts, long start, long end)
	{
		parts[0].StartMs.ShouldBe(start);
		parts[^1].EndMs.ShouldBe(end);
		for (var i = 1; i < parts.Count; i++)
		{
			parts[i].StartMs.ShouldBe(parts[i - 1].EndMs);
		}
	}

	[Fact]
	public void Range_inside_one_segment_is_one_part()
	{
		var member = Member(Span(0, 60_000));

		var parts = EvidenceCoveragePlanner.Plan(12_000, 17_000, [member], D);

		var part = parts.ShouldHaveSingleItem();
		part.StartMs.ShouldBe(12_000);
		part.EndMs.ShouldBe(17_000);
		part.SegmentNumber.ShouldBe(2);
		part.Member.ShouldBeSameAs(member);
		part.Span.ShouldBe(member.Spans[0]);
	}

	[Fact]
	public void Range_across_a_boundary_splits_at_the_nominal_boundary()
	{
		var member = Member(Span(0, 60_000));

		var parts = EvidenceCoveragePlanner.Plan(8_000, 25_000, [member], D);

		Shape(parts).Select(p => (p.Start, p.End, p.N)).ShouldBe(
			[(8_000L, 10_000L, 1), (10_000L, 20_000L, 2), (20_000L, 25_000L, 3)]);
		ShouldBeContiguous(parts, 8_000, 25_000);
	}

	[Fact]
	public void Start_and_end_exactly_at_boundaries_make_whole_segments()
	{
		var member = Member(Span(0, 60_000));

		var parts = EvidenceCoveragePlanner.Plan(10_000, 30_000, [member], D);

		Shape(parts).Select(p => (p.Start, p.End, p.N)).ShouldBe([(10_000L, 20_000L, 2), (20_000L, 30_000L, 3)]);
	}

	[Fact]
	public void Boundary_instant_belongs_to_the_following_segment()
	{
		var member = Member(Span(0, 60_000));

		var parts = EvidenceCoveragePlanner.Plan(10_000, 10_001, [member], D);

		parts.ShouldHaveSingleItem().SegmentNumber.ShouldBe(2);
	}

	[Fact]
	public void Span_gap_inside_one_segment_yields_a_not_covered_part()
	{
		var member = Member(Span(0, 12_000), Span(15_000, 10_000, 12_000));

		var parts = EvidenceCoveragePlanner.Plan(10_000, 20_000, [member], D);

		Shape(parts).Select(p => (p.Start, p.End, p.N, Covered: p.Member is not null)).ShouldBe(
			[(10_000L, 12_000L, 2, true), (12_000L, 15_000L, 0, false), (15_000L, 20_000L, 2, true)]);
		parts[0].Span.ShouldBe(member.Spans[0]);
		parts[2].Span.ShouldBe(member.Spans[1]);
		ShouldBeContiguous(parts, 10_000, 20_000);
	}

	[Fact]
	public void Range_not_covered_by_any_member_is_one_not_covered_part()
	{
		var parts = EvidenceCoveragePlanner.Plan(100_000, 120_000, [Member(Span(0, 10_000))], D);

		var part = parts.ShouldHaveSingleItem();
		part.IsCovered.ShouldBeFalse();
		part.Member.ShouldBeNull();
		part.Span.ShouldBeNull();
		part.StartMs.ShouldBe(100_000);
		part.EndMs.ShouldBe(120_000);
	}

	[Fact]
	public void No_members_is_not_covered()
	{
		EvidenceCoveragePlanner.Plan(0, 5_000, [], D).ShouldHaveSingleItem().IsCovered.ShouldBeFalse();
	}

	[Fact]
	public void First_member_in_order_wins_where_coverage_overlaps()
	{
		var first = Member(Span(0, 15_000));
		var second = Member(Span(5_000, 40_000));

		var parts = EvidenceCoveragePlanner.Plan(0, 30_000, [first, second], D);

		Shape(parts).Select(p => (p.Start, p.End, p.N, p.Member == first ? 1 : 2)).ShouldBe(
			[(0L, 10_000L, 1, 1), (10_000L, 15_000L, 2, 1), (15_000L, 20_000L, 2, 2), (20_000L, 30_000L, 3, 2)]);
		ShouldBeContiguous(parts, 0, 30_000);
	}

	[Fact]
	public void Disjoint_members_are_assigned_by_their_own_coverage()
	{
		var first = Member(Span(0, 10_000));
		var second = Member(Span(20_000, 10_000));

		var parts = EvidenceCoveragePlanner.Plan(5_000, 25_000, [first, second], D);

		Shape(parts).Select(p => (p.Start, p.End, p.N, p.Member == first ? "1" : p.Member == second ? "2" : "-"))
			.ShouldBe([(5_000L, 10_000L, 1, "1"), (10_000L, 20_000L, 0, "-"), (20_000L, 25_000L, 3, "2")]);
	}

	[Fact]
	public void Later_member_covers_what_the_first_does_not()
	{
		var first = Member(Span(0, 10_000));
		var second = Member(Span(0, 30_000));

		var parts = EvidenceCoveragePlanner.Plan(0, 20_000, [first, second], D);

		parts[0].Member.ShouldBeSameAs(first);
		parts[1].Member.ShouldBeSameAs(second);
		parts[1].StartMs.ShouldBe(10_000);
	}

	[Fact]
	public void Adjacent_pieces_of_one_span_and_segment_are_merged()
	{
		var member = Member(Span(0, 20_000), Span(5_000, 3_000, 40_000));

		var parts = EvidenceCoveragePlanner.Plan(0, 10_000, [member], D);

		var part = parts.ShouldHaveSingleItem();
		part.Span.ShouldBe(member.Spans[0]);
	}

	[Fact]
	public void Zero_length_spans_are_ignored()
	{
		var member = Member(new TimelineSpan(0, 0, 0));

		EvidenceCoveragePlanner.Plan(0, 5_000, [member], D).ShouldHaveSingleItem().IsCovered.ShouldBeFalse();
	}

	[Fact]
	public void Fact_window_context_is_clipped_at_zero()
	{
		var window = EvidenceCoveragePlanner.ForFact(2_000, 4_000, 5_000, 1_000, 60_000);

		window.ShouldBe(new EvidenceRequestedWindow(0, 5_000, false));
	}

	[Fact]
	public void Fact_window_with_context_is_extended()
	{
		var window = EvidenceCoveragePlanner.ForFact(20_000, 24_000, 3_000, 2_000, 60_000);

		window.ShouldBe(new EvidenceRequestedWindow(17_000, 26_000, false));
	}

	[Fact]
	public void Long_fact_window_is_shortened_from_its_start()
	{
		var window = EvidenceCoveragePlanner.ForFact(10_000, 100_000, 2_000, 0, 30_000);

		window.ShouldBe(new EvidenceRequestedWindow(8_000, 38_000, true));
	}

	[Fact]
	public void Window_of_exactly_the_maximum_is_not_shortened()
	{
		EvidenceCoveragePlanner.ForFact(0, 30_000, 0, 0, 30_000).Shortened.ShouldBeFalse();
	}

	[Fact]
	public void Explicit_range_is_used_as_is()
	{
		EvidenceCoveragePlanner.ForRange(1_000, 9_000).ShouldBe(new EvidenceRequestedWindow(1_000, 9_000, false));
	}

	[Theory]
	[InlineData(EvidencePartState.Available, "available")]
	[InlineData(EvidencePartState.Materializing, "materializing")]
	[InlineData(EvidencePartState.Rematerializing, "rematerializing")]
	[InlineData(EvidencePartState.Failed, "failed")]
	[InlineData(EvidencePartState.NotCovered, "not-covered")]
	public void Part_states_have_their_wire_names(EvidencePartState state, string name) =>
		state.ToWireName().ShouldBe(name);
}
