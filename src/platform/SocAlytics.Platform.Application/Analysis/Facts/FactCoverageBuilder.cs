using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Facts;

// SegmentNumber is 1-based for segment jobs and null for the match job of a node.
public sealed record FactCoverageJob(int? SegmentNumber, LogicalJobState State, bool HasAcceptedResult);

public sealed record FactCoverageInput(
    bool Required,
    IReadOnlyList<FactCoverageJob> Jobs,
    long SegmentDurationMs,
    long MatchCoverageEndMs);

// A half-open member-facing window; it carries no segment, recording, or storage identifier.
public sealed record FactCoverageWindow(long StartMs, long EndMs, CoverageState State);

// Maps a node's logical jobs to ordered half-open match-time windows (research.md R9).
public static class FactCoverageBuilder
{
    public static IReadOnlyList<FactCoverageWindow> Build(FactCoverageInput input, long? fromMs = null, long? toMs = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var windows = new List<FactCoverageWindow>();

        if (!input.Required)
        {
            Add(windows, 0, input.MatchCoverageEndMs, CoverageState.Skipped);
        }
        else
        {
            foreach (var job in input.Jobs)
            {
                if (job.SegmentNumber is { } number)
                {
                    Add(windows, (number - 1) * input.SegmentDurationMs, number * input.SegmentDurationMs, StateOf(job));
                }
                else
                {
                    Add(windows, 0, input.MatchCoverageEndMs, StateOf(job));
                }
            }
        }

        return windows
            .Where(w => new FactRange(w.StartMs, w.EndMs).Overlaps(fromMs, toMs))
            .OrderBy(w => w.StartMs)
            .ThenBy(w => w.EndMs)
            .ToList();
    }

    public static IReadOnlyDictionary<CoverageState, int> CountByState(IEnumerable<FactCoverageWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var counts = Enum.GetValues<CoverageState>().ToDictionary(s => s, _ => 0);
        foreach (var window in windows)
        {
            counts[window.State]++;
        }

        return counts;
    }

    // Empty windows (no mapped coverage, or a non-positive segment duration) are not reported.
    private static void Add(List<FactCoverageWindow> windows, long startMs, long endMs, CoverageState state)
    {
        if (FactRange.TryCreate(startMs, endMs, out _))
        {
            windows.Add(new FactCoverageWindow(startMs, endMs, state));
        }
    }

    private static CoverageState StateOf(FactCoverageJob job)
    {
        if (job.HasAcceptedResult)
        {
            return CoverageState.Accepted;
        }

        return job.State switch
        {
            LogicalJobState.Failed => CoverageState.Failed,
            LogicalJobState.Blocked => CoverageState.Blocked,
            _ => CoverageState.Missing,
        };
    }
}
