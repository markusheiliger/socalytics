using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

public sealed record MaterializedSegmentView(
    Guid RecordingVersionId,
    string TimelineMappingDigest,
    string SegmentationPolicyDigest,
    int SegmentNumber,
    MaterializedSegmentState State,
    long? MediaSizeBytes,
    string? FailureReason,
    bool InUse,
    DateTimeOffset? AvailableAt);

public sealed record MaterializedSegmentListing(Guid MatchId, IReadOnlyList<MaterializedSegmentView> Segments);

internal sealed record SegmentMatchScope(Guid TeamId, IReadOnlyList<Guid> RecordingVersionIds);

internal static class SegmentMatchAccess
{
    // A match the caller cannot see is not found; a visible match needs the Club Admin permission.
    public static async Task<OperationResult<SegmentMatchScope>> AuthorizeAsync(
        IAccessAuthorizer authorizer,
        IRecordingLineageQueries lineage,
        Guid matchId,
        CancellationToken cancellationToken)
    {
        var visible = await authorizer.AuthorizeTeamResourceAsync(
            new TeamOwnedResource(SegmentAuditEvents.MatchResourceType, matchId), TeamPermission.Read, cancellationToken);
        if (!visible.IsGranted)
        {
            return visible.Kind == AccessDecisionKind.NotFound ? OperationFailure.NotFound() : OperationFailure.Forbidden();
        }

        var administer = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource(SegmentAuditEvents.MatchResourceType, matchId.ToString()),
            cancellationToken);
        if (!administer.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var teamId = visible.Scope!.TeamId;
        var view = await lineage.GetMatchLineageAsync(matchId, teamId, cancellationToken);
        var versions = view.RecordingSetVersions
            .SelectMany(set => set.Members)
            .Select(member => member.RecordingVersionId)
            .Distinct()
            .ToList();
        return new SegmentMatchScope(teamId, versions);
    }
}

public sealed class ListMaterializedSegmentsHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRecordingLineageQueries lineage,
    ISegmentStore store)
{
    public async Task<OperationResult<MaterializedSegmentListing>> HandleAsync(Guid matchId, CancellationToken cancellationToken)
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
        var segments = candidates
            .OrderBy(c => c.Segment.Identity.RecordingVersionId)
            .ThenBy(c => c.Segment.Identity.TimelineMappingDigest.ToString(), StringComparer.Ordinal)
            .ThenBy(c => c.Segment.Identity.SegmentationPolicyDigest.ToString(), StringComparer.Ordinal)
            .ThenBy(c => c.Segment.Identity.SegmentNumber)
            .Select(c => new MaterializedSegmentView(
                c.Segment.Identity.RecordingVersionId,
                c.Segment.Identity.TimelineMappingDigest.ToString(),
                c.Segment.Identity.SegmentationPolicyDigest.ToString(),
                c.Segment.Identity.SegmentNumber,
                c.Segment.State,
                c.Segment.State == MaterializedSegmentState.Available ? c.Segment.Association?.MediaSizeBytes : null,
                c.Segment.FailureReason,
                c.InUse,
                c.Segment.AvailableAt))
            .ToList();
        return new MaterializedSegmentListing(matchId, segments);
    }
}
