using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Segments;

public readonly record struct SegmentInterval(long StartMilliseconds, long EndMilliseconds);

public sealed record SegmentWindow(
	SegmentInterval NominalWindow,
	SegmentInterval RequestedSourceInterval,
	IReadOnlyList<TimelineSpan> TimelineSpans);

public sealed record SegmentWindowResult
{
	private SegmentWindowResult(SegmentWindow? window, string? rejectionReason)
	{
		Window = window;
		RejectionReason = rejectionReason;
	}

	public SegmentWindow? Window { get; }

	public string? RejectionReason { get; }

	public bool IsRejected => Window is null;

	public static SegmentWindowResult Accepted(SegmentWindow window) => new(window, null);

	public static SegmentWindowResult Rejected(string reason) => new(null, reason);
}

public static class SegmentWindowCalculator
{
	public static SegmentInterval NominalWindow(int segmentNumber, int durationSeconds)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentNumber, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSeconds, 0);

		var duration = durationSeconds * 1000L;
		return new SegmentInterval((segmentNumber - 1L) * duration, segmentNumber * duration);
	}

	// Mapped spans are 1:1 in rate; a span covers match time [matchStart, matchStart + mediaLength).
	public static SegmentWindowResult Calculate(int segmentNumber, int durationSeconds, IEnumerable<TimelineSpan> spans)
	{
		ArgumentNullException.ThrowIfNull(spans);

		var nominal = NominalWindow(segmentNumber, durationSeconds);
		var intersected = new List<TimelineSpan>();
		foreach (var span in spans)
		{
			var length = span.MediaEndMilliseconds - span.MediaStartMilliseconds;
			if (length <= 0)
			{
				continue;
			}

			var start = Math.Max(span.MatchStartMilliseconds, nominal.StartMilliseconds);
			var end = Math.Min(span.MatchStartMilliseconds + length, nominal.EndMilliseconds);
			if (end <= start)
			{
				continue;
			}

			var mediaStart = span.MediaStartMilliseconds + (start - span.MatchStartMilliseconds);
			intersected.Add(new TimelineSpan(mediaStart, mediaStart + (end - start), start));
		}

		if (intersected.Count == 0)
		{
			return SegmentWindowResult.Rejected(SegmentFailureReasons.CoverageEmpty);
		}

		intersected.Sort(static (a, b) =>
		{
			var byMatch = a.MatchStartMilliseconds.CompareTo(b.MatchStartMilliseconds);
			return byMatch != 0 ? byMatch : a.MediaStartMilliseconds.CompareTo(b.MediaStartMilliseconds);
		});

		var requested = new SegmentInterval(
			intersected.Min(s => s.MediaStartMilliseconds),
			intersected.Max(s => s.MediaEndMilliseconds));
		return SegmentWindowResult.Accepted(new SegmentWindow(nominal, requested, intersected));
	}
}
