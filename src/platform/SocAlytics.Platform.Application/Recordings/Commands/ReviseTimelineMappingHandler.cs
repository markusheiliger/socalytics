using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Commands;

public sealed record ReviseTimelineMappingCommand(
    Guid MatchId,
    Guid RecordingVersionId,
    string? IdempotencyKey,
    IReadOnlyList<TimelineSpanSeconds>? Spans);

public sealed record ReviseTimelineMappingResult(StoredTimelineMapping Mapping, bool Created);

public sealed class ReviseTimelineMappingHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IRecordingRetryOutcomeStore retryOutcomes,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<ReviseTimelineMappingResult>> HandleAsync(
        ReviseTimelineMappingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var key = command.IdempotencyKey;
        OperationFailure? invalid = null;
        TimelineMapping? mapping = null;
        var digest = string.Empty;
        if (key is null || key.Length is < 1 or > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            invalid = OperationFailure.Validation(new Abstractions.FieldViolation("Idempotency-Key", "invalid"));
        }
        else if (command.Spans is null
            || !TimelineMapping.TryCreate(command.Spans, out mapping, out _))
        {
            invalid = OperationFailure.Validation(
                "timeline-mapping-invalid", new Abstractions.FieldViolation("timelineMapping", "timeline-mapping-invalid"));
        }
        else
        {
            digest = CanonicalJson.Digest(
                RecordingRetryOperation.ReviseTimelineMapping,
                new Dictionary<string, string>
                {
                    ["matchId"] = command.MatchId.ToString(),
                    ["recordingVersionId"] = command.RecordingVersionId.ToString(),
                },
                new JsonObject { ["timelineMappingDigest"] = mapping.Digest.ToString() }).ToString();
        }

        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
            if (!decision.IsGranted)
            {
                return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
            }

            if (decision.Scope!.SeasonState == SeasonState.Archived)
            {
                return OperationFailure.Conflict("season-archived");
            }

            if (!await recordings.RecordingVersionExistsAsync(command.MatchId, command.RecordingVersionId, cancellationToken))
            {
                return OperationFailure.NotFound();
            }

            if (invalid is not null)
            {
                return invalid;
            }

            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.ReviseTimelineMapping, command.MatchId, key!, cancellationToken);
            if (outcome is not null)
            {
                return await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
            }

            var actor = requestContext.MemberAccountId!.Value;
            var now = time.GetUtcNow();
            var teamId = decision.Scope.TeamId;
            var existing = await recordings.FindMappingByDigestAsync(
                command.MatchId, command.RecordingVersionId, mapping!.Digest.ToString(), cancellationToken);
            var created = existing is null;
            var stored = existing ?? new StoredTimelineMapping(
                Guid.CreateVersion7(), command.RecordingVersionId, command.MatchId, mapping.Spans, mapping.Digest.ToString(), now);
            if (created)
            {
                await recordings.InsertTimelineMappingAsync(stored, mapping.CanonicalJson, actor, cancellationToken);
            }

            await retryOutcomes.InsertAsync(
                RecordingRetryOperation.ReviseTimelineMapping,
                command.MatchId,
                key!,
                digest,
                created ? 201 : 200,
                new JsonObject { ["timelineMappingId"] = stored.Id.ToString() }.ToJsonString(),
                actor,
                now,
                cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = RecordingAuditActions.ReviseTimelineMappingEvent,
                    Action = RecordingAuditActions.ReviseTimelineMappingAction,
                    Outcome = created ? AuditOutcome.Succeeded : AuditOutcome.Unchanged,
                    Resource = RecordingAuditActions.TimelineMapping(stored.Id),
                    TeamId = teamId,
                    Details = new Dictionary<string, string> { [RecordingAuditActions.MatchIdDetail] = command.MatchId.ToString() },
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return new ReviseTimelineMappingResult(stored, created);
        }
        catch (DuplicateRetryKeyException)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.ReviseTimelineMapping, command.MatchId, key!, cancellationToken);
            return outcome is null
                ? OperationFailure.Conflict("idempotency-key-reused")
                : await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
        }
    }

    private async Task<OperationResult<ReviseTimelineMappingResult>> ReplayAsync(
        RecordingRetryOutcome outcome, string digest, Guid matchId, CancellationToken cancellationToken)
    {
        if (!string.Equals(outcome.RequestDigest, digest, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict("idempotency-key-reused");
        }

        var id = Guid.Parse(JsonNode.Parse(outcome.Result)!["timelineMappingId"]!.GetValue<string>());
        var stored = await recordings.FindMappingAsync(matchId, id, cancellationToken);
        return stored is null
            ? OperationFailure.NotFound()
            : new ReviseTimelineMappingResult(stored, outcome.StatusCode == 201);
    }
}
