using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Commands;

public sealed record StartRecordingUploadCommand(
    Guid MatchId,
    string? IdempotencyKey,
    string? DisplayName,
    string? Description,
    string? ContentType,
    long TotalSizeBytes,
    long PartSizeBytes,
    IReadOnlyList<string>? PartDigests);

/// <summary>Grants are empty when the session is no longer pending or has expired.</summary>
public sealed record StartRecordingUploadResult(
    UploadSession Session,
    UploadSessionState State,
    IReadOnlyList<PartUploadGrant> Grants,
    bool Replayed);

public sealed class StartRecordingUploadHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IRecordingRetryOutcomeStore retryOutcomes,
    IObjectStorage storage,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    IOptions<RecordingUploadOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<StartRecordingUploadResult>> HandleAsync(
        StartRecordingUploadCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = options.Value;

        var validated = Validate(command, settings);
        string digest = string.Empty;
        if (validated.Failure is null)
        {
            digest = RequestDigest(command.MatchId, validated.Descriptor!, validated.Declaration!);
        }

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
            if (!decision.IsGranted)
            {
                return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
            }

            if (decision.Scope!.SeasonState == SeasonState.Archived)
            {
                return OperationFailure.Conflict("season-archived");
            }

            if (validated.Failure is not null)
            {
                return validated.Failure;
            }

            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.StartUpload, command.MatchId, command.IdempotencyKey!, cancellationToken);
            if (outcome is not null)
            {
                return await ReplayAsync(outcome, digest, command.MatchId, settings, cancellationToken);
            }
        }

        var descriptor = validated.Descriptor!;
        var declaration = validated.Declaration!;
        var key = command.IdempotencyKey!;
        var sessionId = Guid.CreateVersion7();
        var objectKey = UploadSession.BuildObjectKey(settings.KeyPrefix, command.MatchId, sessionId);
        var reference = await storage.InitiateCompositeMultipartUploadAsync(objectKey, descriptor.ContentType, cancellationToken);

        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
            if (!decision.IsGranted)
            {
                await AbortQuietlyAsync(reference);
                return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
            }

            var teamScope = decision.Scope!;
            if (teamScope.SeasonState == SeasonState.Archived)
            {
                await AbortQuietlyAsync(reference);
                return OperationFailure.Conflict("season-archived");
            }

            var now = time.GetUtcNow();
            var actor = requestContext.MemberAccountId!.Value;
            var session = UploadSession.Start(
                sessionId, command.MatchId, teamScope.TeamId, descriptor, declaration, settings.KeyPrefix,
                reference.UploadId, actor, now, settings.SessionLifetime);
            await recordings.InsertUploadSessionAsync(session, cancellationToken);
            await retryOutcomes.InsertAsync(
                RecordingRetryOperation.StartUpload,
                command.MatchId,
                key,
                digest,
                201,
                new JsonObject { ["uploadSessionId"] = sessionId.ToString() }.ToJsonString(),
                actor,
                now,
                cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = RecordingAuditActions.StartUploadEvent,
                    Action = RecordingAuditActions.StartUploadAction,
                    Outcome = AuditOutcome.Succeeded,
                    Resource = RecordingAuditActions.UploadSession(sessionId),
                    TeamId = teamScope.TeamId,
                    Details = new Dictionary<string, string>
                    {
                        [RecordingAuditActions.MatchIdDetail] = command.MatchId.ToString(),
                        [RecordingAuditActions.PartCountDetail] = declaration.PartCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return Build(session, now, settings, false);
        }
        catch (DuplicateRetryKeyException)
        {
            await AbortQuietlyAsync(reference);
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.StartUpload, command.MatchId, key, cancellationToken);
            return outcome is null
                ? OperationFailure.Conflict("idempotency-key-reused")
                : await ReplayAsync(outcome, digest, command.MatchId, settings, cancellationToken);
        }
        catch
        {
            await AbortQuietlyAsync(reference);
            throw;
        }
    }

    private async Task AbortQuietlyAsync(MultipartUploadReference reference)
    {
        try
        {
            await storage.AbortMultipartUploadAsync(reference, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort; the expiry sweep releases leftover storage.
        }
    }

    private async Task<OperationResult<StartRecordingUploadResult>> ReplayAsync(
        RecordingRetryOutcome outcome, string digest, Guid matchId, RecordingUploadOptions settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(outcome.RequestDigest, digest, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict("idempotency-key-reused");
        }

        var uploadSessionId = Guid.Parse(JsonNode.Parse(outcome.Result)!["uploadSessionId"]!.GetValue<string>());
        var stored = await recordings.FindUploadSessionAsync(matchId, uploadSessionId, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        return Build(stored.Session, stored.DatabaseNow, settings, true);
    }

    private StartRecordingUploadResult Build(UploadSession session, DateTimeOffset now, RecordingUploadOptions settings, bool replayed)
    {
        var state = session.IsReportedExpired(now) ? UploadSessionState.Expired : session.State;
        if (!session.CanIssueGrants(now))
        {
            return new StartRecordingUploadResult(session, state, [], replayed);
        }

        var expires = Min(now + settings.GrantLifetime, session.ExpiresAt);
        var count = Math.Min(session.Declaration.PartCount, settings.MaxGrantsPerRequest);
        var reference = new MultipartUploadReference(session.ObjectKey, session.MultipartUploadId);
        var grants = new List<PartUploadGrant>(count);
        for (var n = 1; n <= count; n++)
        {
            grants.Add(storage.PresignUploadPart(reference, n, session.Declaration.PartSize(n), session.Declaration.PartDigests[n - 1], expires));
        }

        return new StartRecordingUploadResult(session, state, grants, replayed);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static string RequestDigest(Guid matchId, RecordingDescriptor descriptor, MultipartDeclaration declaration)
    {
        var digests = new JsonArray();
        foreach (var d in declaration.PartDigests)
        {
            digests.Add(d.ToString());
        }

        var body = new JsonObject
        {
            ["displayName"] = descriptor.DisplayName,
            ["description"] = descriptor.Description,
            ["contentType"] = descriptor.ContentType,
            ["totalSizeBytes"] = declaration.TotalSizeBytes,
            ["partSizeBytes"] = declaration.PartSizeBytes,
            ["partDigests"] = digests,
        };
        return CanonicalJson.Digest(
            RecordingRetryOperation.StartUpload,
            new Dictionary<string, string> { ["matchId"] = matchId.ToString() },
            body).ToString();
    }

    private sealed record Validated(OperationFailure? Failure, RecordingDescriptor? Descriptor, MultipartDeclaration? Declaration);

    private static Validated Validate(StartRecordingUploadCommand command, RecordingUploadOptions settings)
    {
        var key = command.IdempotencyKey;
        if (key is null || key.Length is < 1 or > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            return new(OperationFailure.Validation(new Abstractions.FieldViolation("Idempotency-Key", "invalid")), null, null);
        }

        if (!RecordingDescriptor.TryCreate(command.DisplayName, command.Description, command.ContentType, out var descriptor, out var descriptorViolations))
        {
            return new(OperationFailure.Validation([.. descriptorViolations.Select(v => new Abstractions.FieldViolation(v.Field, v.Message))]), null, null);
        }

        if (!settings.AllowedContentTypes.Contains(descriptor!.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return new(OperationFailure.Validation("content-type-not-allowed", new Abstractions.FieldViolation("contentType", "content-type-not-allowed")), null, null);
        }

        var bounds = new MultipartBounds(settings.MaxObjectSizeBytes, settings.MinPartSizeBytes, settings.MaxPartSizeBytes, settings.MaxPartCount);
        if (!MultipartDeclaration.TryCreate(command.TotalSizeBytes, command.PartSizeBytes, command.PartDigests, bounds, out var declaration, out var violations))
        {
            return new(OperationFailure.Validation("upload-declaration-invalid", [.. violations.Select(v => new Abstractions.FieldViolation(v.Field, v.Message))]), null, null);
        }

        return new(null, descriptor, declaration);
    }
}
