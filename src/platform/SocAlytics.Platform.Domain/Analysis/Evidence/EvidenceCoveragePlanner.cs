using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Domain.Analysis.Evidence;

/// <summary>A recording-set member as the planner sees it: identity, timeline-mapping digest, and mapped spans.</summary>
public sealed record EvidencePlanMember(
	Guid RecordingVersionId,
	string TimelineMappingDigest,
	IReadOnlyList<TimelineSpan> Spans);

public readonly record struct EvidenceRequestedWindow(long StartMs, long EndMs, bool Shortened);

/// <summary>
/// One half-open match-time part. A covered part belongs to one member, one mapped span, and one segment number;
/// an uncovered part has none of them and is <see cref="EvidencePartState.NotCovered"/>.
/// </summary>
public sealed record EvidencePlannedPart(
	long StartMs,
	long EndMs,
	EvidencePlanMember? Member,
	TimelineSpan? Span,
	int SegmentNumber)
{
	public bool IsCovered => Member is not null;
}

public static class EvidenceCoveragePlanner
{
	public static EvidenceRequestedWindow ForFact(
		long factStartMs,
		long factEndMs,
		long contextBeforeMs,
		long contextAfterMs,
		long maxDurationMs)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(factStartMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factEndMs, factStartMs);
		ArgumentOutOfRangeException.ThrowIfNegative(contextBeforeMs);
		ArgumentOutOfRangeException.ThrowIfNegative(contextAfterMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDurationMs, 0);

		var start = Math.Max(0, factStartMs - contextBeforeMs);
		var end = factEndMs + contextAfterMs;
		return end - start > maxDurationMs
			? new EvidenceRequestedWindow(start, start + maxDurationMs, true)
			: new EvidenceRequestedWindow(start, end, false);
	}

	public static EvidenceRequestedWindow ForRange(long fromMs, long toMs)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(fromMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toMs, fromMs);
		return new EvidenceRequestedWindow(fromMs, toMs, false);
	}

	public static IReadOnlyList<EvidencePlannedPart> Plan(
		long startMs,
		long endMs,
		IReadOnlyList<EvidencePlanMember> members,
		int segmentDurationSeconds)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(startMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endMs, startMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentDurationSeconds, 0);
		ArgumentNullException.ThrowIfNull(members);

		var cuts = new SortedSet<long> { startMs, endMs };
		foreach (var member in members)
		{
			foreach (var span in member.Spans)
			{
				if (Length(span) <= 0)
				{
					continue;
				}

				AddWithin(cuts, span.MatchStartMilliseconds, startMs, endMs);
				AddWithin(cuts, span.MatchStartMilliseconds + Length(span), startMs, endMs);
			}
		}

		var duration = segmentDurationSeconds * 1000L;
		var number = (int)(startMs / duration) + 1;
		for (var boundary = SegmentWindowCalculator.NominalWindow(number, segmentDurationSeconds).EndMilliseconds;
			boundary < endMs;
			boundary = SegmentWindowCalculator.NominalWindow(++number, segmentDurationSeconds).EndMilliseconds)
		{
			cuts.Add(boundary);
		}

		var points = cuts.ToArray();
		var parts = new List<EvidencePlannedPart>();
		for (var i = 0; i < points.Length - 1; i++)
		{
			var cell = Assign(points[i], points[i + 1], members, duration);
			if (parts.Count > 0 && CanMerge(parts[^1], cell))
			{
				parts[^1] = parts[^1] with { EndMs = cell.EndMs };
			}
			else
			{
				parts.Add(cell);
			}
		}

		return parts;
	}

	private static EvidencePlannedPart Assign(
		long start,
		long end,
		IReadOnlyList<EvidencePlanMember> members,
		long duration)
	{
		foreach (var member in members)
		{
			foreach (var span in member.Spans)
			{
				if (Length(span) > 0
					&& span.MatchStartMilliseconds <= start
					&& end <= span.MatchStartMilliseconds + Length(span))
				{
					return new EvidencePlannedPart(start, end, member, span, (int)(start / duration) + 1);
				}
			}
		}

		return new EvidencePlannedPart(start, end, null, null, 0);
	}

	private static bool CanMerge(EvidencePlannedPart previous, EvidencePlannedPart next) =>
		previous.IsCovered
			? next.IsCovered
				&& ReferenceEquals(previous.Member, next.Member)
				&& previous.Span == next.Span
				&& previous.SegmentNumber == next.SegmentNumber
			: !next.IsCovered;

	private static long Length(TimelineSpan span) => span.MediaEndMilliseconds - span.MediaStartMilliseconds;

	private static void AddWithin(SortedSet<long> cuts, long value, long start, long end)
	{
		if (value > start && value < end)
		{
			cuts.Add(value);
		}
	}
}
