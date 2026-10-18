using Shouldly;
using SocAlytics.Platform.Domain.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Domain;

public sealed class FactRangeTests
{
	[Fact]
	public void Accepts_a_non_negative_start_before_the_end()
	{
		var range = new FactRange(0, 1);
		range.StartMs.ShouldBe(0);
		range.EndMs.ShouldBe(1);
	}

	[Theory]
	[InlineData(-1, 5)]
	[InlineData(5, 5)]
	[InlineData(6, 5)]
	public void Rejects_negative_start_and_empty_or_inverted_ranges(long start, long end)
	{
		Should.Throw<ArgumentOutOfRangeException>(() => new FactRange(start, end));
		FactRange.TryCreate(start, end, out _).ShouldBeFalse();
	}

	[Fact]
	public void TryCreate_returns_the_range_when_valid()
	{
		FactRange.TryCreate(2, 9, out var range).ShouldBeTrue();
		range.ShouldBe(new FactRange(2, 9));
	}

	[Theory]
	[InlineData(10, 20, true)]
	[InlineData(11, 19, true)]
	[InlineData(9, 20, false)]
	[InlineData(10, 21, false)]
	[InlineData(20, 30, false)]
	[InlineData(0, 10, false)]
	public void IsWithin_contains_a_range_in_a_half_open_window(long start, long end, bool expected)
	{
		new FactRange(start, end).IsWithin(new FactRange(10, 20)).ShouldBe(expected);
	}

	[Fact]
	public void Fact_ending_at_the_window_end_is_inside_and_one_starting_there_is_outside()
	{
		var window = new FactRange(100, 200);
		new FactRange(150, 200).IsWithin(window).ShouldBeTrue();
		new FactRange(200, 201).IsWithin(window).ShouldBeFalse();
		new FactRange(99, 150).IsWithin(window).ShouldBeFalse();
		new FactRange(100, 200).IsWithin(window).ShouldBeTrue();
	}

	[Theory]
	[InlineData(10, 20, 20L, 30L, false)]
	[InlineData(10, 20, 0L, 10L, false)]
	[InlineData(10, 20, 19L, 30L, true)]
	[InlineData(10, 20, 0L, 11L, true)]
	[InlineData(10, 20, 12L, 15L, true)]
	[InlineData(10, 20, 0L, 100L, true)]
	[InlineData(10, 20, null, 10L, false)]
	[InlineData(10, 20, null, 11L, true)]
	[InlineData(10, 20, 20L, null, false)]
	[InlineData(10, 20, 19L, null, true)]
	[InlineData(10, 20, null, null, true)]
	public void Overlaps_uses_half_open_semantics(long start, long end, long? from, long? to, bool expected)
	{
		new FactRange(start, end).Overlaps(from, to).ShouldBe(expected);
	}
}
