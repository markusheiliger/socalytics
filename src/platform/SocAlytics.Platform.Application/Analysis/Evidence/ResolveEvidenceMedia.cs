using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Analysis.Evidence;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using FieldViolation = SocAlytics.Platform.Application.Abstractions.FieldViolation;

namespace SocAlytics.Platform.Application.Analysis.Evidence;

public sealed record EvidenceFactRequest(Guid? AcceptedResultId, int? Ordinal, long? ContextBeforeMs, long? ContextAfterMs);

public sealed record EvidenceRangeRequest(long? FromMs, long? ToMs);

public sealed record ResolveEvidenceMediaCommand(
    Guid RunId, EvidenceFactRequest? Fact, EvidenceRangeRequest? Range, bool RetryFailed);

public sealed record EvidenceMediaPiece(
    Uri Url,
    DateTimeOffset ExpiresAt,
    string MediaType,
    string MediaDigest,
    long MediaSizeBytes,
    long StartOffsetMs,
    long EndOffsetMs,
    Guid RecordingVersionId);

public sealed record EvidencePartView(
    long StartMs,
    long EndMs,
    EvidencePartState State,
    string? Reason,
    int? RetryAfterSeconds,
    EvidenceMediaPiece? Media);

public sealed record EvidenceResolution(
    Guid RunId,
    Guid MatchId,
    Guid TeamId,
    long RequestedFromMs,
    long RequestedToMs,
    long? FactStartMs,
    long? FactEndMs,
    bool Shortened,
    IReadOnlyList<EvidencePartView> Parts);

// Resolves a fact or range of a run to ordered parts with short-lived read links. The links exist only in the result.
public sealed class ResolveEvidenceMediaHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalysisRunStore runs,
    IFactQueries facts,
    IRecordingLineageQueries lineage,
    ISegmentStore segments,
    RequestSegmentMaterializationHandler materialization,
    IObjectStorage storage,
    IContractValidator contracts,
    IAuditTrail audit,
    IOptions<EvidenceOptions> options,
    TimeProvider time)
{
    public const string StorageUnavailableCode = "evidence-storage-unavailable";
    private const long MaxManifestBytes = 64 * 1024;

    private sealed record SegmentOutcome(
        EvidencePartState State,
        string? Reason,
        SegmentManifestOffsets? Offsets,
        MaterializedSegmentRecord? Record,
        ObjectReadGrant? Grant);

    public async Task<OperationResult<EvidenceResolution>> HandleAsync(
        ResolveEvidenceMediaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var settings = options.Value;
        var maxDurationMs = (long)settings.MaxDuration!.Value.TotalMilliseconds;
        var maxContextMs = (long)settings.MaxContext!.Value.TotalMilliseconds;
        var violations = Validate(command, maxDurationMs, maxContextMs);
        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        var invalidated = new List<MaterializedSegmentRecord>();
        EvidenceResolution resolution;
        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var decision = await authorizer.AuthorizeTeamResourceAsync(
                new(EvidenceAuditEvents.RunResourceType, command.RunId), TeamPermission.Read, cancellationToken);
            if (!decision.IsGranted)
            {
                return OperationFailure.NotFound();
            }

            var hierarchy = await runs.LoadAsync(command.RunId, cancellationToken);
            if (hierarchy is null)
            {
                return OperationFailure.NotFound();
            }

            var run = hierarchy.Run;
            FactTimeRange? fact = null;
            EvidenceRequestedWindow window;
            if (command.Fact is { } factRequest)
            {
                fact = await facts.GetRunFactAsync(
                    command.RunId, factRequest.AcceptedResultId!.Value, factRequest.Ordinal!.Value, cancellationToken);
                if (fact is null)
                {
                    return OperationFailure.NotFound();
                }

                window = EvidenceCoveragePlanner.ForFact(
                    fact.StartMs, fact.EndMs, factRequest.ContextBeforeMs ?? 0, factRequest.ContextAfterMs ?? 0, maxDurationMs);
            }
            else
            {
                window = EvidenceCoveragePlanner.ForRange(command.Range!.FromMs!.Value, command.Range.ToMs!.Value);
            }

            var policy = ReadPolicy(hierarchy.Snapshot.SnapshotJson);
            var set = await lineage.GetSetVersionAsync(run.MatchId, run.RecordingSetVersionId, cancellationToken);
            if (set is null || policy is null)
            {
                return OperationFailure.NotFound();
            }

            var members = new List<EvidencePlanMember>();
            foreach (var member in set.Members.OrderBy(m => m.Position))
            {
                var mapping = await lineage.GetTimelineMappingAsync(run.MatchId, member.TimelineMappingId, cancellationToken);
                members.Add(new EvidencePlanMember(member.RecordingVersionId, member.TimelineMappingDigest, mapping?.Spans ?? []));
            }

            var planned = EvidenceCoveragePlanner.Plan(window.StartMs, window.EndMs, members, policy.DurationSeconds);
            var outcomes = new Dictionary<(Guid, int), SegmentOutcome>();
            var parts = new List<EvidencePartView>();
            var retryAfter = (int)Math.Ceiling(settings.RetryAfter!.Value.TotalSeconds);
            var grantExpiry = time.GetUtcNow() + settings.GrantLifetime!.Value;

            foreach (var part in planned)
            {
                if (!part.IsCovered)
                {
                    parts.Add(new EvidencePartView(part.StartMs, part.EndMs, EvidencePartState.NotCovered, null, null, null));
                    continue;
                }

                var key = (part.Member!.RecordingVersionId, part.SegmentNumber);
                if (!outcomes.TryGetValue(key, out var outcome))
                {
                    outcome = await ResolveSegmentAsync(
                        run, set.MatchId, set.Id, part, policy, command.RetryFailed, grantExpiry, invalidated, cancellationToken);
                    outcomes[key] = outcome;
                }

                parts.Add(ToView(part, outcome, retryAfter));
            }

            var granted = parts.Where(p => p.Media is not null).Select(p => p.Media!).ToList();
            if (granted.Count > 0)
            {
                await audit.RecordAsync(
                    EvidenceAuditEvents.Granted(
                        run.Id,
                        run.MatchId,
                        run.TeamId,
                        command.Fact?.AcceptedResultId,
                        command.Fact?.Ordinal,
                        window.StartMs,
                        window.EndMs,
                        window.Shortened,
                        [.. granted.Select(m => m.MediaDigest).Distinct(StringComparer.Ordinal)],
                        granted.Max(m => m.ExpiresAt)),
                    cancellationToken);
            }

            await scope.CommitAsync(cancellationToken);
            resolution = new EvidenceResolution(
                run.Id, run.MatchId, run.TeamId, window.StartMs, window.EndMs, fact?.StartMs, fact?.EndMs, window.Shortened, parts);
        }
        catch (ObjectStorageUnavailableException)
        {
            return OperationFailure.Unavailable(StorageUnavailableCode);
        }

        await InvalidateAsync(invalidated, cancellationToken);
        return resolution;
    }

    private static List<FieldViolation> Validate(ResolveEvidenceMediaCommand command, long maxDurationMs, long maxContextMs)
    {
        var violations = new List<FieldViolation>();
        if ((command.Fact is null) == (command.Range is null))
        {
            violations.Add(new FieldViolation("fact", "exactly-one-of-fact-and-range"));
            violations.Add(new FieldViolation("range", "exactly-one-of-fact-and-range"));
            return violations;
        }

        if (command.Fact is { } fact)
        {
            if (fact.AcceptedResultId is null || fact.AcceptedResultId == Guid.Empty)
            {
                violations.Add(new FieldViolation("fact.acceptedResultId", "required"));
            }

            if (fact.Ordinal is null)
            {
                violations.Add(new FieldViolation("fact.ordinal", "required"));
            }
            else if (fact.Ordinal < 0)
            {
                violations.Add(new FieldViolation("fact.ordinal", "out-of-range"));
            }

            if (fact.ContextBeforeMs is < 0 || fact.ContextBeforeMs > maxContextMs)
            {
                violations.Add(new FieldViolation("fact.contextBeforeMs", "out-of-range"));
            }

            if (fact.ContextAfterMs is < 0 || fact.ContextAfterMs > maxContextMs)
            {
                violations.Add(new FieldViolation("fact.contextAfterMs", "out-of-range"));
            }
        }
        else
        {
            var range = command.Range!;
            if (range.FromMs is null)
            {
                violations.Add(new FieldViolation("range.fromMs", "required"));
            }
            else if (range.FromMs < 0)
            {
                violations.Add(new FieldViolation("range.fromMs", "out-of-range"));
            }

            if (range.ToMs is null)
            {
                violations.Add(new FieldViolation("range.toMs", "required"));
            }
            else if (range.FromMs is { } from)
            {
                if (range.ToMs <= from)
                {
                    violations.Add(new FieldViolation("range.toMs", "out-of-range"));
                }
                else if (range.ToMs - from > maxDurationMs)
                {
                    violations.Add(new FieldViolation("range.toMs", "range-too-long"));
                }
            }
        }

        return violations;
    }

    private sealed record Policy(int Version, int DurationSeconds, Sha256Digest Digest);

    private static Policy? ReadPolicy(string snapshotJson)
    {
        using var snapshot = JsonDocument.Parse(snapshotJson);
        if (!snapshot.RootElement.TryGetProperty("segmentationPolicy", out var policy)
            || !Sha256Digest.TryParse(policy.GetProperty("digest").GetString(), out var digest))
        {
            return null;
        }

        return new Policy(policy.GetProperty("version").GetInt32(), policy.GetProperty("durationSeconds").GetInt32(), digest);
    }

    private async Task<SegmentOutcome> ResolveSegmentAsync(
        AnalysisRunRecord run,
        Guid matchId,
        Guid recordingSetVersionId,
        EvidencePlannedPart part,
        Policy policy,
        bool retryFailed,
        DateTimeOffset grantExpiry,
        List<MaterializedSegmentRecord> invalidated,
        CancellationToken cancellationToken)
    {
        var member = part.Member!;
        if (!Sha256Digest.TryParse(member.TimelineMappingDigest, out var mappingDigest))
        {
            return new SegmentOutcome(EvidencePartState.Failed, SegmentFailureReasons.ScopeMismatch, null, null, null);
        }

        var identity = new MaterializedSegmentIdentity(member.RecordingVersionId, mappingDigest, policy.Digest, part.SegmentNumber);
        var record = await segments.FindAsync(identity, cancellationToken);
        switch (record?.State)
        {
            case MaterializedSegmentState.Materializing:
                return new SegmentOutcome(Preparing(record), null, null, record, null);
            case MaterializedSegmentState.Failed when !retryFailed:
                return new SegmentOutcome(EvidencePartState.Failed, record.FailureReason, null, record, null);
            case MaterializedSegmentState.Available:
                return await CheckAvailableAsync(record, part, grantExpiry, invalidated, cancellationToken);
        }

        var request = await materialization.HandleAsync(
            new RequestSegmentMaterializationCommand(
                matchId, run.TeamId, recordingSetVersionId, member.RecordingVersionId, member.TimelineMappingDigest,
                policy.Digest.ToString(), part.SegmentNumber, policy.Version, policy.DurationSeconds),
            cancellationToken);
        return request.IsAccepted
            ? new SegmentOutcome(Preparing(record), null, null, record, null)
            : new SegmentOutcome(EvidencePartState.Failed, request.RejectionReason, null, record, null);
    }

    private static EvidencePartState Preparing(MaterializedSegmentRecord? record) =>
        record?.PurgedAt is null ? EvidencePartState.Materializing : EvidencePartState.Rematerializing;

    private async Task<SegmentOutcome> CheckAvailableAsync(
        MaterializedSegmentRecord record,
        EvidencePlannedPart part,
        DateTimeOffset grantExpiry,
        List<MaterializedSegmentRecord> invalidated,
        CancellationToken cancellationToken)
    {
        var association = record.Association!;
        var media = await storage.GetObjectChecksumAsync(association.MediaObjectKey, cancellationToken);
        var manifestRead = media is not null
            && string.Equals(media.Digest.ToString(), association.MediaDigest, StringComparison.Ordinal)
            && media.ContentLength == association.MediaSizeBytes
                ? await storage.ReadObjectAsync(association.ManifestObjectKey, MaxManifestBytes, cancellationToken)
                : null;

        SegmentManifestOffsets? offsets = null;
        if (manifestRead?.Content is { } content
            && string.Equals(Sha256Digest.FromBytes(SHA256.HashData(content)).ToString(), association.ManifestChecksum, StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(content);
            offsets = SegmentManifestOffsets.TryParse(document.RootElement, contracts);
        }

        if (offsets is null
            || !string.Equals(offsets.MediaDigest, association.MediaDigest, StringComparison.Ordinal)
            || offsets.MediaSizeBytes != association.MediaSizeBytes
            || !offsets.TryTranslate(part.StartMs, part.EndMs, out _, out _))
        {
            invalidated.Add(record);
            return new SegmentOutcome(EvidencePartState.Failed, SegmentFailureReasons.MediaInvalid, null, record, null);
        }

        var grant = storage.PresignGetObject(association.MediaObjectKey, grantExpiry);
        return new SegmentOutcome(EvidencePartState.Available, null, offsets, record, grant);
    }

    private EvidencePartView ToView(
        EvidencePlannedPart part, SegmentOutcome outcome, int retryAfterSeconds)
    {
        switch (outcome.State)
        {
            case EvidencePartState.Available:
                if (!outcome.Offsets!.TryTranslate(part.StartMs, part.EndMs, out var start, out var end))
                {
                    return new EvidencePartView(
                        part.StartMs, part.EndMs, EvidencePartState.Failed, SegmentFailureReasons.MediaInvalid, null, null);
                }

                return new EvidencePartView(
                    part.StartMs, part.EndMs, EvidencePartState.Available, null, null,
                    new EvidenceMediaPiece(
                        outcome.Grant!.Url,
                        outcome.Grant.ExpiresAt,
                        outcome.Offsets.MediaType,
                        outcome.Offsets.MediaDigest,
                        outcome.Offsets.MediaSizeBytes,
                        start,
                        end,
                        part.Member!.RecordingVersionId));
            case EvidencePartState.Materializing:
            case EvidencePartState.Rematerializing:
                return new EvidencePartView(part.StartMs, part.EndMs, outcome.State, null, retryAfterSeconds, null);
            default:
                return new EvidencePartView(part.StartMs, part.EndMs, outcome.State, outcome.Reason, null, null);
        }
    }

    // The segment fails and its objects are deleted so the next demand materializes it again.
    private async Task InvalidateAsync(List<MaterializedSegmentRecord> records, CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            SegmentAssociation? previous;
            await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
            {
                previous = await segments.TryInvalidateAvailableAsync(
                    record.Id, record.Association!.MediaDigest, SegmentFailureReasons.MediaInvalid, cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            if (previous is not null)
            {
                await storage.DeleteObjectAsync(previous.MediaObjectKey, CancellationToken.None);
                await storage.DeleteObjectAsync(previous.ManifestObjectKey, CancellationToken.None);
            }
        }
    }
}
