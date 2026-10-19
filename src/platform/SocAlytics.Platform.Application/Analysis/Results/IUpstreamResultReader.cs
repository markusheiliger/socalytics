using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Results;

// SegmentIds and the match range are mutually exclusive; the range is half-open and selects overlapping segment windows.
public sealed record UpstreamReadRequest(
    Guid JobId,
    string UpstreamNodeKey,
    IReadOnlyList<string>? SegmentIds,
    long? MatchStartMs,
    long? MatchEndMs,
    int PageSize,
    string? ContinuationToken);

// SegmentId and the window are null for the result of a match-scope upstream.
public sealed record UpstreamResultItem(
    Guid AcceptedResultId,
    string? SegmentId,
    long? WindowStartMs,
    long? WindowEndMs,
    string ManifestReference,
    string ManifestChecksum,
    AcceptedResultManifestRecord Manifest);

public sealed record UpstreamResultsPage(
    string SnapshotStatus,
    IReadOnlyList<UpstreamResultItem> Results,
    IReadOnlyList<CoverageEntry> Coverage,
    string? ContinuationToken,
    CoverageMode CoverageMode = CoverageMode.Complete,
    decimal? MinimumAcceptedFraction = null);

public sealed class InvalidContinuationTokenException() : Exception("The continuation token is not valid.");

public interface IUpstreamResultReader
{
    // Returns null when the job has no snapshot entry for the upstream node. Results are exactly the accepted results
    // of that snapshot entry, ordered by segment window start and then accepted-result id.
    Task<UpstreamResultsPage?> ReadAsync(UpstreamReadRequest request, CancellationToken cancellationToken);
}
