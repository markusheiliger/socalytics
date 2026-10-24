using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

public sealed record SegmentPurgeSummary(Guid MatchId, int Purged, int InUse);

public sealed class PurgeMaterializedSegmentsHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRecordingLineageQueries lineage,
    ISegmentStore store,
    IObjectStorage storage,
    IAuditTrail audit)
{
    public const string StorageUnavailable = "segment-storage-unavailable";

    public async Task<OperationResult<SegmentPurgeSummary>> HandleAsync(Guid matchId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var access = await SegmentMatchAccess.AuthorizeAsync(authorizer, lineage, matchId, cancellationToken);
        if (!access.IsSuccess)
        {
            return access.Failure;
        }

        IReadOnlyList<SegmentPurgeCandidate> candidates = access.Value.RecordingVersionIds.Count == 0
            ? []
            : await store.LockForPurgeAsync(access.Value.RecordingVersionIds, cancellationToken);
        var inUse = candidates.Count(c => c.InUse);
        var purged = 0;
        var unavailable = false;

        // Rows purged before a storage failure stay purged; the rest are left unchanged.
        var purgeable = candidates.Where(c =>
            !c.InUse && c.Segment.State is MaterializedSegmentState.Available or MaterializedSegmentState.Failed);
        foreach (var candidate in purgeable)
        {
            try
            {
                if (candidate.Segment.Association is { } association)
                {
                    await storage.DeleteObjectAsync(association.MediaObjectKey, cancellationToken);
                    await storage.DeleteObjectAsync(association.ManifestObjectKey, cancellationToken);
                }
            }
            catch (ObjectStorageUnavailableException)
            {
                unavailable = true;
                break;
            }

            if (await store.TryPurgeAsync(candidate.Segment.Id, cancellationToken))
            {
                purged++;
            }
        }

        if (!unavailable || purged > 0)
        {
            await audit.RecordAsync(SegmentAuditEvents.Purged(matchId, access.Value.TeamId, purged, inUse), cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        return unavailable
            ? OperationFailure.Unavailable(StorageUnavailable)
            : new SegmentPurgeSummary(matchId, purged, inUse);
    }
}
