using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Commands;

public sealed record CompleteRecordingUploadCommand(
    Guid MatchId,
    Guid UploadSessionId,
    string? IdempotencyKey,
    IReadOnlyList<TimelineSpanSeconds>? Spans);

public sealed record CompleteRecordingUploadResult(RecordingVersion Version, StoredTimelineMapping Mapping, bool Replayed);

public sealed class CompleteRecordingUploadHandler(
    IUnitOfWork unitOfWork,
    IRecordingStore recordings,
    IRecordingRetryOutcomeStore retryOutcomes,
    IObjectStorage storage,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<CompleteRecordingUploadResult>> HandleAsync(
        CompleteRecordingUploadCommand command, CancellationToken cancellationToken)
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
            || !TimelineMapping.TryCreate(command.Spans, out mapping, out var violations))
        {
            invalid = OperationFailure.Validation(
                "timeline-mapping-invalid", new Abstractions.FieldViolation("timelineMapping", "timeline-mapping-invalid"));
        }
        else
        {
            digest = CanonicalJson.Digest(
                RecordingRetryOperation.CompleteUpload,
                new Dictionary<string, string>
                {
                    ["matchId"] = command.MatchId.ToString(),
                    ["uploadSessionId"] = command.UploadSessionId.ToString(),
                },
                new JsonObject { ["timelineMappingDigest"] = mapping.Digest.ToString() }).ToString();
        }

        UploadSession upload;
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

            if (invalid is not null)
            {
                return invalid;
            }

            var found = await recordings.FindUploadSessionAsync(command.MatchId, command.UploadSessionId, cancellationToken);
            if (found is null)
            {
                return OperationFailure.NotFound();
            }

            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.CompleteUpload, command.MatchId, key!, cancellationToken);
            if (outcome is not null)
            {
                return await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
            }

            if (found.Session.State == UploadSessionState.Completed)
            {
                return OperationFailure.Conflict("upload-session-completed");
            }

            if (!found.Session.CanComplete(found.DatabaseNow))
            {
                return OperationFailure.Conflict("upload-session-expired");
            }

            upload = found.Session;
        }

        var verified = await VerifyStorageAsync(upload, cancellationToken);
        if (verified.Failure is not null)
        {
            return verified.Failure;
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

            if (!await recordings.TryCompleteUploadSessionAsync(upload.Id, cancellationToken))
            {
                return OperationFailure.Conflict("upload-session-expired");
            }

            var now = time.GetUtcNow();
            var actor = requestContext.MemberAccountId!.Value;
            var version = RecordingVersion.FromCompletedUpload(
                Guid.CreateVersion7(), upload, upload.Declaration.ExpectedDigest, verified.ETag, actor, now);
            var stored = new StoredTimelineMapping(
                Guid.CreateVersion7(), version.Id, version.MatchId, mapping!.Spans, mapping.Digest.ToString(), now);
            await recordings.InsertCompletedUploadAsync(version, stored, mapping.CanonicalJson, cancellationToken);
            await retryOutcomes.InsertAsync(
                RecordingRetryOperation.CompleteUpload,
                command.MatchId,
                key!,
                digest,
                201,
                new JsonObject
                {
                    ["recordingVersionId"] = version.Id.ToString(),
                    ["timelineMappingId"] = stored.Id.ToString(),
                }.ToJsonString(),
                actor,
                now,
                cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = RecordingAuditActions.CompleteUploadEvent,
                    Action = RecordingAuditActions.CompleteUploadAction,
                    Outcome = AuditOutcome.Succeeded,
                    Resource = RecordingAuditActions.RecordingVersion(version.Id),
                    TeamId = decision.Scope.TeamId,
                    Details = new Dictionary<string, string> { [RecordingAuditActions.MatchIdDetail] = command.MatchId.ToString() },
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return new CompleteRecordingUploadResult(version, stored, false);
        }
        catch (DuplicateRetryKeyException)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var outcome = await retryOutcomes.FindAsync(RecordingRetryOperation.CompleteUpload, command.MatchId, key!, cancellationToken);
            return outcome is null
                ? OperationFailure.Conflict("idempotency-key-reused")
                : await ReplayAsync(outcome, digest, command.MatchId, cancellationToken);
        }
    }

    private sealed record Verified(OperationFailure? Failure, string? ETag);

    private async Task<Verified> VerifyStorageAsync(UploadSession upload, CancellationToken cancellationToken)
    {
        var reference = new MultipartUploadReference(upload.ObjectKey, upload.MultipartUploadId);
        var declaration = upload.Declaration;
        try
        {
            var stored = await storage.ListPartsAsync(reference, null, cancellationToken);
            var byNumber = stored.GroupBy(p => p.PartNumber).ToDictionary(g => g.Key, g => g.ToList());
            for (var n = 1; n <= declaration.PartCount; n++)
            {
                if (!byNumber.ContainsKey(n))
                {
                    return new(OperationFailure.Conflict("upload-parts-incomplete"), null);
                }
            }

            if (stored.Count != declaration.PartCount
                || stored.Any(p => p.SizeBytes != declaration.PartSize(p.PartNumber)))
            {
                return new(OperationFailure.Conflict("upload-part-mismatch"), null);
            }

            var entries = stored
                .OrderBy(p => p.PartNumber)
                .Select(p => new CompletedPartEntry(p.PartNumber, p.ETag, declaration.PartDigests[p.PartNumber - 1]))
                .ToList();
            try
            {
                await storage.CompleteMultipartUploadAsync(reference, entries, declaration.ExpectedDigest, declaration.TotalSizeBytes, cancellationToken);
            }
            catch (ObjectStorageRejectedException ex) when (ex.Reason == ObjectStorageRejectionReason.NoSuchUpload)
            {
            }
        }
        catch (ObjectStorageRejectedException ex) when (ex.Reason is ObjectStorageRejectionReason.InvalidPart
            or ObjectStorageRejectionReason.BadDigest or ObjectStorageRejectionReason.EntityTooSmall)
        {
            return new(OperationFailure.Conflict("upload-part-mismatch"), null);
        }
        catch (ObjectStorageRejectedException ex) when (ex.Reason == ObjectStorageRejectionReason.NoSuchUpload)
        {
        }

        var evidence = await storage.GetIntegrityEvidenceAsync(upload.ObjectKey, cancellationToken);
        if (evidence is null || string.IsNullOrEmpty(evidence.Checksum))
        {
            return new(OperationFailure.Conflict("integrity-evidence-unavailable"), null);
        }

        if (!declaration.ExpectedDigest.MatchesS3Checksum(evidence.Checksum, evidence.ChecksumType)
            || evidence.ContentLength != declaration.TotalSizeBytes)
        {
            return new(OperationFailure.Conflict("upload-object-mismatch"), null);
        }

        return new(null, evidence.ETag);
    }

    private async Task<OperationResult<CompleteRecordingUploadResult>> ReplayAsync(
        RecordingRetryOutcome outcome, string digest, Guid matchId, CancellationToken cancellationToken)
    {
        if (!string.Equals(outcome.RequestDigest, digest, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict("idempotency-key-reused");
        }

        var json = JsonNode.Parse(outcome.Result)!;
        var versionId = Guid.Parse(json["recordingVersionId"]!.GetValue<string>());
        var mappingId = Guid.Parse(json["timelineMappingId"]!.GetValue<string>());
        var stored = await recordings.FindCompletedUploadAsync(matchId, versionId, mappingId, cancellationToken);
        return stored is null
            ? OperationFailure.NotFound()
            : new CompleteRecordingUploadResult(stored.Version, stored.Mapping, true);
    }
}
