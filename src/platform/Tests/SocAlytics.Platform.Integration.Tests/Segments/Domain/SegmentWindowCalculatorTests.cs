using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments.Domain;

public sealed class SegmentWindowCalculatorTests
{
	private static SegmentWindow Accept(int n, int d, params TimelineSpan[] spans)
	{
		var result = SegmentWindowCalculator.Calculate(n, d, spans);
		result.IsRejected.ShouldBeFalse();
		return result.Window!;
	}

	[Theory]
	[InlineData(1, 10, 0, 10_000)]
	[InlineData(2, 10, 10_000, 20_000)]
	[InlineData(7, 5, 30_000, 35_000)]
	public void Nominal_window_is_half_open_in_match_milliseconds(int n, int d, long start, long end) =>
		SegmentWindowCalculator.NominalWindow(n, d).ShouldBe(new SegmentInterval(start, end));

	[Fact]
	public void Boundary_instant_belongs_to_the_following_segment()
	{
		var spans = new[] { new TimelineSpan(0, 10_000, 0) };

		Accept(1, 10, spans).RequestedSourceInterval.ShouldBe(new SegmentInterval(0, 10_000));
		SegmentWindowCalculator.Calculate(2, 10, spans).RejectionReason.ShouldBe(SegmentFailureReasons.CoverageEmpty);

		var starting = new[] { new TimelineSpan(500, 3_500, 10_000) };
		SegmentWindowCalculator.Calculate(1, 10, starting).IsRejected.ShouldBeTrue();
		Accept(2, 10, starting).RequestedSourceInterval.ShouldBe(new SegmentInterval(500, 3_500));
	}

	[Fact]
	public void Span_is_clipped_to_the_nominal_window_and_translated_one_to_one()
	{
		var window = Accept(2, 10, new TimelineSpan(1_000, 31_000, 4_000));

		window.NominalWindow.ShouldBe(new SegmentInterval(10_000, 20_000));
		window.TimelineSpans.ShouldBe([new TimelineSpan(7_000, 17_000, 10_000)]);
		window.RequestedSourceInterval.ShouldBe(new SegmentInterval(7_000, 17_000));
	}

	[Fact]
	public void Multiple_spans_are_covered_by_the_smallest_contiguous_media_interval()
	{
		var window = Accept(
			1,
			10,
			new TimelineSpan(50_000, 52_000, 6_000),
			new TimelineSpan(1_000, 3_000, 0),
			new TimelineSpan(20_000, 20_000, 4_000));

		window.TimelineSpans.ShouldBe([new TimelineSpan(1_000, 3_000, 0), new TimelineSpan(50_000, 52_000, 6_000)]);
		window.RequestedSourceInterval.ShouldBe(new SegmentInterval(1_000, 52_000));
	}

	[Fact]
	public void Empty_intersection_is_rejected()
	{
		var result = SegmentWindowCalculator.Calculate(3, 10, [new TimelineSpan(0, 5_000, 0)]);

		result.IsRejected.ShouldBeTrue();
		result.Window.ShouldBeNull();
		result.RejectionReason.ShouldBe(SegmentFailureReasons.CoverageEmpty);
		SegmentWindowCalculator.Calculate(1, 10, []).RejectionReason.ShouldBe(SegmentFailureReasons.CoverageEmpty);
	}

	[Theory]
	[InlineData(0, 10)]
	[InlineData(-1, 10)]
	[InlineData(1, 0)]
	[InlineData(1, -5)]
	public void Nonpositive_values_are_refused(int n, int d)
	{
		Should.Throw<ArgumentOutOfRangeException>(() => SegmentWindowCalculator.Calculate(n, d, [new TimelineSpan(0, 1_000, 0)]));
		Should.Throw<ArgumentOutOfRangeException>(() => SegmentWindowCalculator.NominalWindow(n, d));
	}

	[Fact]
	public void Numbering_agrees_with_the_lineage_segment_source()
	{
		var spans = new[] { new TimelineSpan(0, 25_000, 8_000) };
		var numbers = Enumerable.Range(1, 6)
			.Where(n => !SegmentWindowCalculator.Calculate(n, 10, spans).IsRejected)
			.ToArray();

		numbers.ShouldBe([1, 2, 3, 4]);
	}

	[Fact]
	public void Identity_requires_all_fields_and_a_positive_number()
	{
		var digest = Sha256Digest.FromBytes(new byte[32]);
		var id = Guid.NewGuid();

		new MaterializedSegmentIdentity(id, digest, digest, 1).SegmentNumber.ShouldBe(1);
		Should.Throw<ArgumentException>(() => new MaterializedSegmentIdentity(Guid.Empty, digest, digest, 1));
		Should.Throw<ArgumentNullException>(() => new MaterializedSegmentIdentity(id, null!, digest, 1));
		Should.Throw<ArgumentNullException>(() => new MaterializedSegmentIdentity(id, digest, null!, 1));
		Should.Throw<ArgumentOutOfRangeException>(() => new MaterializedSegmentIdentity(id, digest, digest, 0));
	}

	[Fact]
	public void States_and_failure_reasons_match_the_data_model()
	{
		foreach (var state in Enum.GetValues<MaterializedSegmentState>())
		{
			MaterializedSegmentStates.TryParse(MaterializedSegmentStates.ToPersistedName(state), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(state);
		}

		MaterializedSegmentStates.TryParse("other", out _).ShouldBeFalse();
		SegmentFailureReasons.All.ShouldBe(
		[
			"segment-coverage-empty", "segment-coverage-incomplete", "segmentation-policy-unknown",
			"segmentation-policy-conflict", "segment-scope-mismatch", "source-unavailable", "toolchain-unavailable",
			"extraction-failed", "storage-unavailable", "segment-too-large", "segment-media-invalid",
			"retry-budget-exhausted",
		]);
		SegmentFailureReasons.IsKnown("nope").ShouldBeFalse();
	}
}
