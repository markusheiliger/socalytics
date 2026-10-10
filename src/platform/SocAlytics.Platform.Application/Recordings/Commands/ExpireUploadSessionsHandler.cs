using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.Recordings.Commands;

/// <summary>Expires due upload sessions and releases their storage; idempotent and state-guarded for concurrent instances.</summary>
public sealed class ExpireUploadSessionsHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IObjectStorage storage,
    IAuditTrail audit)
{
    private const int BatchSize = 100;

    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var expired = 0;
        IReadOnlyList<Guid> due;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await recordings.ListDueUploadSessionIdsAsync(BatchSize, cancellationToken);
        }

        foreach (var id in due)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var transitioned = await recordings.TryExpireUploadSessionAsync(id, cancellationToken);
            if (transitioned is null)
            {
                continue;
            }

            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = RecordingAuditActions.ExpireUploadEvent,
                    Action = RecordingAuditActions.ExpireUploadSessionAction,
                    Outcome = AuditOutcome.Succeeded,
                    Resource = RecordingAuditActions.UploadSession(id),
                    TeamId = transitioned.TeamId,
                    Details = new Dictionary<string, string> { [RecordingAuditActions.MatchIdDetail] = transitioned.MatchId.ToString() },
                    ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            expired++;
        }

        IReadOnlyList<UnreleasedUploadStorage> pending;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            pending = await recordings.ListUnreleasedExpiredUploadsAsync(BatchSize, cancellationToken);
        }

        foreach (var upload in pending)
        {
            await storage.AbortMultipartUploadAsync(new MultipartUploadReference(upload.ObjectKey, upload.MultipartUploadId), cancellationToken);
            await storage.DeleteObjectAsync(upload.ObjectKey, cancellationToken);
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            if (await recordings.TryMarkStorageReleasedAsync(upload.Id, cancellationToken))
            {
                await scope.CommitAsync(cancellationToken);
            }
        }

        return expired;
    }
}
