using Shouldly;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Infrastructure.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Domain;

public sealed class LogicalJobPlanningTests
{
	private static readonly string DigestText = Sha256Digest.Prefix + new string('a', 64);
	private static readonly Sha256Digest Digest = Parse(DigestText);

	private static Sha256Digest Parse(string text)
	{
		Sha256Digest.TryParse(text, out var d).ShouldBeTrue();
		return d!;
	}

	private static RecordingSetLineageMember Member(int position, params TimelineSpan[] spans) =>
		new(position, Guid.NewGuid(), DigestText, Guid.NewGuid(), DigestText, spans);

	private static IReadOnlyList<AnalysisSegmentReference> Segments(params RecordingSetLineageMember[] members) =>
		new LineageAnalysisSegmentSource().GetSegments(
			new RecordingSetLineage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, members), 300, Digest);

	[Fact]
	public void Ninety_minute_member_yields_18_segments() =>
		Segments(Member(1, new TimelineSpan(0, 5_400_000, 0))).Count.ShouldBe(18);

	[Fact]
	public void Offset_member_yields_segments_one_to_three()
	{
		var segments = Segments(Member(1, new TimelineSpan(0, 490_000, 120_000)));
		segments.Select(s => s.SegmentNumber).ShouldBe([1, 2, 3]);
	}

	[Fact]
	public void Gap_covering_a_whole_window_yields_no_segment()
	{
		var segments = Segments(Member(1, new TimelineSpan(0, 100_000, 0), new TimelineSpan(0, 100_000, 650_000)));
		segments.Select(s => s.SegmentNumber).ShouldBe([1, 3]);
	}

	[Fact]
	public void Members_produce_independent_segment_sets()
	{
		var a = Member(1, new TimelineSpan(0, 600_000, 0));
		var b = Member(2, new TimelineSpan(0, 300_000, 0));
		var segments = Segments(a, b);
		segments.Count(s => s.RecordingVersionId == a.RecordingVersionId).ShouldBe(2);
		segments.Count(s => s.RecordingVersionId == b.RecordingVersionId).ShouldBe(1);
	}

	private static PinnedJobSnapshot Snapshot(string profile) =>
		new(3, 30, 90, 3600, profile, Digest, Digest, null, new Dictionary<string, string>(), "1");

	[Fact]
	public void Plans_segment_jobs_plus_match_job_with_identical_snapshots()
	{
		var low = Snapshot("low");
		var high = Snapshot("high");
		var segments = Segments(Member(1, new TimelineSpan(0, 900_000, 0)));
		var jobs = LogicalJobPlanner.Plan(
			[new PlannerNode("low", ExecutionScope.Segment, low), new PlannerNode("high", ExecutionScope.Match, high)],
			segments);

		jobs.Count.ShouldBe(4);
		jobs.Where(j => j.NodeKey == "low").ShouldAllBe(j => j.Snapshot == low && j.Segment != null);
		jobs.Single(j => j.NodeKey == "high").Snapshot.ShouldBe(high);
	}

	[Fact]
	public void Planned_job_exposes_no_host_manager_or_runtime_member()
	{
		var names = typeof(PlannedLogicalJob).GetProperties().Concat(typeof(PinnedJobSnapshot).GetProperties()).Select(p => p.Name);
		foreach (var forbidden in new[] { "Host", "Manager", "Runtime", "Accelerator", "Preprocessing" })
		{
			names.ShouldNotContain(n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
		}
	}
}
