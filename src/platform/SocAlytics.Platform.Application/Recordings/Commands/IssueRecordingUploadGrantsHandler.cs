using System.Globalization;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Commands;

public sealed record IssueRecordingUploadGrantsCommand(Guid MatchId, Guid UploadSessionId, IReadOnlyList<int>? PartNumbers);

public sealed record IssueRecordingUploadGrantsResult(IReadOnlyList<PartUploadGrant> Grants);

public sealed class IssueRecordingUploadGrantsHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IObjectStorage storage,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    IOptions<RecordingUploadOptions> options)
{
    private const int MaxRequestedParts = 1000;

    public async Task<OperationResult<IssueRecordingUploadGrantsResult>> HandleAsync(
        IssueRecordingUploadGrantsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = options.Value;

        StoredUploadSession stored;
        Guid teamId;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
            if (!decision.IsGranted)
            {
                return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
            }

            if (decision.Scope!.SeasonState == SeasonState.Archived)
            {
                return OperationFailure.Conflict("season-archived");
            }

            teamId = decision.Scope.TeamId;
            var found = await recordings.FindUploadSessionAsync(command.MatchId, command.UploadSessionId, cancellationToken);
            if (found is null)
            {
                return OperationFailure.NotFound();
            }

            stored = found;
        }

        var session = stored.Session;
        var now = stored.DatabaseNow;
        if (session.State == UploadSessionState.Completed)
        {
            return OperationFailure.Conflict("upload-session-completed");
        }

        if (!session.CanIssueGrants(now))
        {
            return OperationFailure.Conflict("upload-session-expired");
        }

        var parts = command.PartNumbers;
        var partCount = session.Declaration.PartCount;
        if (parts is null || parts.Count == 0 || parts.Count > Math.Min(MaxRequestedParts, settings.MaxGrantsPerRequest)
            || parts.Distinct().Count() != parts.Count || parts.Any(n => n < 1 || n > partCount))
        {
            return OperationFailure.Validation("part-numbers-invalid", new Abstractions.FieldViolation("partNumbers", "part-numbers-invalid"));
        }

        var reference = new MultipartUploadReference(session.ObjectKey, session.MultipartUploadId);
        await storage.ListPartsAsync(reference, 1, cancellationToken);

        var expires = now + settings.GrantLifetime <= session.ExpiresAt ? now + settings.GrantLifetime : session.ExpiresAt;
        var grants = parts
            .Select(n => storage.PresignUploadPart(reference, n, session.Declaration.PartSize(n), session.Declaration.PartDigests[n - 1], expires))
            .ToList();

        await audit.RecordIndependentAsync(
            new AuditEvent
            {
                EventType = RecordingAuditActions.IssueGrantsEvent,
                Action = RecordingAuditActions.IssueGrantsAction,
                Outcome = AuditOutcome.Succeeded,
                Resource = RecordingAuditActions.UploadSession(session.Id),
                TeamId = teamId,
                Details = new Dictionary<string, string>
                {
                    [RecordingAuditActions.MatchIdDetail] = command.MatchId.ToString(),
                    [RecordingAuditActions.GrantedPartCountDetail] = grants.Count.ToString(CultureInfo.InvariantCulture),
                    [RecordingAuditActions.GrantExpiresAtDetail] = expires.ToString("O", CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);

        return new IssueRecordingUploadGrantsResult(grants);
    }
}
