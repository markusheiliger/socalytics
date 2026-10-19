using Shouldly;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Domain.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Facts;

public sealed class FactCoverageBuilderTests
{
    private const long Duration = 300_000;
    private const long MatchEnd = 900_000;

    private static FactCoverageInput Required(params FactCoverageJob[] jobs) => new(true, jobs, Duration, MatchEnd);

    [Theory]
    [InlineData(LogicalJobState.Waiting, false, CoverageState.Missing)]
    [InlineData(LogicalJobState.Ready, false, CoverageState.Missing)]
    [InlineData(LogicalJobState.Claimed, false, CoverageState.Missing)]
    [InlineData(LogicalJobState.Completed, false, CoverageState.Missing)]
    [InlineData(LogicalJobState.Failed, false, CoverageState.Failed)]
    [InlineData(LogicalJobState.Blocked, false, CoverageState.Blocked)]
    [InlineData(LogicalJobState.Completed, true, CoverageState.Accepted)]
    [InlineData(LogicalJobState.Failed, true, CoverageState.Accepted)]
    public void Maps_segment_job_state_to_the_window_state(LogicalJobState state, bool accepted, CoverageState expected)
    {
        var windows = FactCoverageBuilder.Build(Required(new FactCoverageJob(2, state, accepted)));

        windows.ShouldBe([new FactCoverageWindow(Duration, 2 * Duration, expected)]);
    }

    [Fact]
    public void Match_job_covers_the_mapped_match_time_coverage()
    {
        var windows = FactCoverageBuilder.Build(Required(new FactCoverageJob(null, LogicalJobState.Blocked, false)));

        windows.ShouldBe([new FactCoverageWindow(0, MatchEnd, CoverageState.Blocked)]);
    }

    [Fact]
    public void Node_not_required_by_the_run_reports_one_skipped_window_for_the_match()
    {
        var input = new FactCoverageInput(false, [], Duration, MatchEnd);

        FactCoverageBuilder.Build(input).ShouldBe([new FactCoverageWindow(0, MatchEnd, CoverageState.Skipped)]);
    }

    [Fact]
    public void Orders_windows_by_start_regardless_of_job_order()
    {
        var windows = FactCoverageBuilder.Build(Required(
            new FactCoverageJob(3, LogicalJobState.Ready, false),
            new FactCoverageJob(1, LogicalJobState.Completed, true),
            new FactCoverageJob(2, LogicalJobState.Failed, false)));

        windows.Select(w => w.StartMs).ShouldBe([0, Duration, 2 * Duration]);
        windows.Select(w => w.State).ShouldBe([CoverageState.Accepted, CoverageState.Failed, CoverageState.Missing]);
    }

    [Fact]
    public void Filters_to_windows_overlapping_the_half_open_range()
    {
        var input = Required(
            new FactCoverageJob(1, LogicalJobState.Completed, true),
            new FactCoverageJob(2, LogicalJobState.Completed, true),
            new FactCoverageJob(3, LogicalJobState.Completed, true));

        FactCoverageBuilder.Build(input, Duration, 2 * Duration).Select(w => w.StartMs).ShouldBe([Duration]);
        FactCoverageBuilder.Build(input, Duration - 1, Duration + 1).Select(w => w.StartMs).ShouldBe([0, Duration]);
        FactCoverageBuilder.Build(input, 2 * Duration, null).Select(w => w.StartMs).ShouldBe([2 * Duration]);
        FactCoverageBuilder.Build(input, null, Duration).Select(w => w.StartMs).ShouldBe([0]);
        FactCoverageBuilder.Build(input, 3 * Duration, null).ShouldBeEmpty();
        FactCoverageBuilder.Build(input).Count.ShouldBe(3);
    }

    [Fact]
    public void Omits_empty_windows_when_no_match_time_coverage_is_mapped()
    {
        var input = new FactCoverageInput(true, [new FactCoverageJob(null, LogicalJobState.Ready, false)], Duration, 0);

        FactCoverageBuilder.Build(input).ShouldBeEmpty();
    }

    [Fact]
    public void Counts_windows_per_state_including_zero_states()
    {
        var windows = FactCoverageBuilder.Build(Required(
            new FactCoverageJob(1, LogicalJobState.Completed, true),
            new FactCoverageJob(2, LogicalJobState.Completed, true),
            new FactCoverageJob(3, LogicalJobState.Failed, false)));

        var counts = FactCoverageBuilder.CountByState(windows);

        counts[CoverageState.Accepted].ShouldBe(2);
        counts[CoverageState.Failed].ShouldBe(1);
        counts[CoverageState.Missing].ShouldBe(0);
        counts[CoverageState.Blocked].ShouldBe(0);
        counts[CoverageState.Skipped].ShouldBe(0);
    }

    [Fact]
    public void Windows_expose_only_match_time_and_state()
    {
        typeof(FactCoverageWindow).GetProperties().Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ShouldBe(["StartMs", "EndMs", "State"], ignoreOrder: true);
    }
}
