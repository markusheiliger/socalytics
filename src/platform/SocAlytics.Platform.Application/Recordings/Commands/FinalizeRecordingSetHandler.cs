using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Commands;

public sealed record FinalizeRecordingSetMember(Guid RecordingVersionId, Guid TimelineMappingId);

public sealed record FinalizeRecordingSetCommand(
    Guid MatchId,
    string? IdempotencyKey,
    IReadOnlyList<FinalizeRecordingSetMember>? Members);

public sealed record FinalizeRecordingSetResult(RecordingSetVersionView Set, bool Replayed);

public sealed class FinalizeRecordingSetHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IRecordingLineageQueries lineage,
    IRecordingRetryOutcomeStore retryOutcomes,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    IOutbox outbox,
    IOptions<RecordingSetOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<FinalizeRecordingSetResult>> HandleAsync(
        FinalizeRecordingSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var key = command.IdempotencyKey;
        var max = options.Value.MaxMembers;
        OperationFailure? invalid = null;
        var digest = string.Empty;
        if (key is null || key.Length is < 1 or > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            invalid = OperationFailure.Validation(new Abstractions.FieldViolation("Idempotency-Key", "invalid"));
        }
        else if (command.Members is null)
        {
            invalid = OperationFailure.Validation(new Abstractions.FieldViolation("members", "required"));
        }
        else if (command.Members.Count > max)
        {
            invalid = OperationFailure.Validation(
                RecordingSetVersion.TooLargeCode, new Abstractions.FieldViolation("members", RecordingSetVersion.TooLargeCode));
        }
        else
        {
            var body = new JsonObject
            {
                ["members"] = new JsonArray([.. command.Members.Select(m => (JsonNode)new JsonObject
                {
                    ["recordingVersionId"] = m.RecordingVersionId.ToString(),
                    ["timelineMappingId"] = m.TimelineMappingId.ToString(),
                })]),
            };
            digest = CanonicalJson.Digest(
                RecordingRetryOperation.FinalizeRecordingSet,
                new Dictionary<string, string> { ["matchId"] = command.MatchId.ToString() },
                body).ToString();
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

            if (invalid is not null)
            {
                return invalid;
            }

            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.FinalizeRecordingSet, command.MatchId, key!, cancellationToken);
            if (outcome is not null)
            {
                return await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
            }

            var requested = command.Members!.Select(m => (m.RecordingVersionId, m.TimelineMappingId)).ToList();
            var matchMappings = await recordings.GetMatchMappingsAsync(
                command.MatchId, [.. requested.Select(r => r.RecordingVersionId).Distinct()], cancellationToken);
            var now = time.GetUtcNow();
            now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
            if (!RecordingSetVersion.TryCreate(
                Guid.CreateVersion7(), command.MatchId, now, requested, matchMappings, max, out var set, out var violations))
            {
                var first = violations[0].Code;
                var code = first is RecordingSetVersion.EmptyCode or RecordingSetVersion.TooLargeCode ? first : "validation-failed";
                return OperationFailure.Validation(
                    code, [.. violations.Select(v => new Abstractions.FieldViolation(v.Field, v.Code))]);
            }

            var actor = requestContext.MemberAccountId!.Value;
            var teamId = decision.Scope.TeamId;
            await recordings.InsertRecordingSetAsync(set, teamId, actor, cancellationToken);
            var stored = (await lineage.GetSetVersionAsync(command.MatchId, set.Id, cancellationToken))!;
            var eventId = Guid.CreateVersion7();
            var payload = new JsonObject
            {
                ["eventId"] = eventId.ToString(),
                ["eventType"] = "matches.recordings-finalized",
                ["contractVersion"] = "1.0.0",
                ["occurredAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                ["recordingSetVersionId"] = set.Id.ToString(),
                ["matchId"] = command.MatchId.ToString(),
                ["teamId"] = teamId.ToString(),
                ["members"] = new JsonArray([.. stored.Members.Select(m => (JsonNode)new JsonObject
                {
                    ["position"] = m.Position,
                    ["recordingVersionId"] = m.RecordingVersionId.ToString(),
                    ["recordingContentDigest"] = m.RecordingContentDigest,
                    ["timelineMappingId"] = m.TimelineMappingId.ToString(),
                    ["timelineMappingDigest"] = m.TimelineMappingDigest,
                })]),
            };
            var payloadJson = payload.ToJsonString();
            await recordings.InsertFinalizedEventAsync(eventId, set.Id, command.MatchId, teamId, now, payloadJson, cancellationToken);
            using (var payloadDocument = System.Text.Json.JsonDocument.Parse(payloadJson))
            {
                await outbox.EnqueueAsync(
                    new OutboxMessageDraft(
                        eventId,
                        OutboxMessageTypes.RecordingsFinalized,
                        "1.0.0",
                        OutboxMessageTypes.RecordingsFinalized,
                        payloadDocument.RootElement.Clone(),
                        eventId),
                    cancellationToken);
            }

            await retryOutcomes.InsertAsync(
                RecordingRetryOperation.FinalizeRecordingSet,
                command.MatchId,
                key!,
                digest,
                201,
                new JsonObject { ["recordingSetVersionId"] = set.Id.ToString() }.ToJsonString(),
                actor,
                now,
                cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = RecordingAuditActions.FinalizeRecordingSetEvent,
                    Action = RecordingAuditActions.FinalizeRecordingSetAction,
                    Outcome = AuditOutcome.Succeeded,
                    Resource = RecordingAuditActions.RecordingSetVersion(set.Id),
                    TeamId = teamId,
                    Details = new Dictionary<string, string>
                    {
                        [RecordingAuditActions.MatchIdDetail] = command.MatchId.ToString(),
                        [RecordingAuditActions.MemberCountDetail] = set.MemberCount.ToString(CultureInfo.InvariantCulture),
                    },
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return new FinalizeRecordingSetResult(stored, false);
        }
        catch (DuplicateRetryKeyException)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.FinalizeRecordingSet, command.MatchId, key!, cancellationToken);
            return outcome is null
                ? OperationFailure.Conflict("idempotency-key-reused")
                : await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
        }
    }

    private async Task<OperationResult<FinalizeRecordingSetResult>> ReplayAsync(
        RecordingRetryOutcome outcome, string digest, Guid matchId, CancellationToken cancellationToken)
    {
        if (!string.Equals(outcome.RequestDigest, digest, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict("idempotency-key-reused");
        }

        var id = Guid.Parse(JsonNode.Parse(outcome.Result)!["recordingSetVersionId"]!.GetValue<string>());
        var set = await lineage.GetSetVersionAsync(matchId, id, cancellationToken);
        return set is null ? OperationFailure.NotFound() : new FinalizeRecordingSetResult(set, true);
    }
}
