using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Results;

public enum CoverageState
{
    Accepted,
    Missing,
    Failed,
    Blocked,
    Skipped,
}

// SegmentId is null for the single target of a match-scope upstream.
public sealed record CoverageTarget(string? SegmentId, bool CoveredBySnapshotResult, LogicalJobState UpstreamJobState);

public sealed record CoverageEntry(string? SegmentId, CoverageState State);

// Applies the coverage state mapping of the upstream results read (data-model.md).
public static class CoverageBuilder
{
    public static IReadOnlyList<CoverageEntry> Build(string snapshotStatus, IEnumerable<CoverageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Select(t => new CoverageEntry(t.SegmentId, StateOf(snapshotStatus, t))).ToList();
    }

    private static CoverageState StateOf(string snapshotStatus, CoverageTarget target)
    {
        if (target.CoveredBySnapshotResult)
        {
            return CoverageState.Accepted;
        }

        if (snapshotStatus == SnapshotStatus.NotApplicable)
        {
            return CoverageState.Skipped;
        }

        return target.UpstreamJobState switch
        {
            LogicalJobState.Blocked => CoverageState.Blocked,
            LogicalJobState.Failed => CoverageState.Failed,
            _ => CoverageState.Missing,
        };
    }
}
