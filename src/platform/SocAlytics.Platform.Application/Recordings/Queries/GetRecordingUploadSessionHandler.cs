using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Queries;

public sealed record GetRecordingUploadSessionQuery(Guid MatchId, Guid UploadSessionId);

/// <summary>The session with its reported state; the stored <see cref="UploadSession.Version"/> is the ETag.</summary>
public sealed record RecordingUploadSessionView(UploadSession Session, UploadSessionState State);

public sealed class GetRecordingUploadSessionHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<RecordingUploadSessionView>> HandleAsync(
        GetRecordingUploadSessionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var stored = await recordings.FindUploadSessionAsync(query.MatchId, query.UploadSessionId, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        var state = stored.Session.IsReportedExpired(stored.DatabaseNow) ? UploadSessionState.Expired : stored.Session.State;
        return new RecordingUploadSessionView(stored.Session, state);
    }
}
