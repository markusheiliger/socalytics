using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using FieldViolation = SocAlytics.Platform.Application.Abstractions.FieldViolation;

namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed record GetSegmentMediaQuery(Guid AttemptId, long? FencingToken);

public sealed record SegmentMediaGrant(string Name, Uri Url, DateTimeOffset ExpiresAt);

public sealed record GetSegmentMediaResult(
    Guid AttemptId,
    Guid LogicalJobId,
    Guid RunId,
    string ManifestChecksum,
    JsonElement Manifest,
    IReadOnlyList<SegmentMediaGrant> Grants);

// Serves the segment media a job was pinned to: the manifest and two single-object read grants. Source-recording
// locations never leave the platform.
public sealed class GetSegmentMediaHandler(
    IUnitOfWork unitOfWork,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    ISegmentStore segments,
    IObjectStorage storage,
    IAuditTrail audit,
    ICurrentAnalystManager manager,
    IOptions<AnalysisResultOptions> options,
    TimeProvider time)
{
    public const string NotApplicableCode = "segment-media-not-applicable";
    public const string InvalidCode = SegmentFailureReasons.MediaInvalid;
    public const string AttemptObsoleteCode = IssueResultGrantsHandler.AttemptObsoleteCode;

    private sealed record Scoped(
        Guid RunId, Guid JobId, SegmentMediaPin Pin, MaterializedSegmentIdentity? Identity, DateTimeOffset GrantsExpireAt);

    public async Task<OperationResult<GetSegmentMediaResult>> HandleAsync(GetSegmentMediaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Guid? refusedRunId = null;
        var refusedJobId = Guid.Empty;
        string? refusalReason = null;
        OperationFailure? failure = null;
        Scoped? scoped = null;

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var attempt = await attempts.LockAsync(query.AttemptId, cancellationToken);
            if (attempt is null)
            {
                return OperationFailure.NotFound();
            }

            var job = await jobs.LockJobAsync(attempt.LogicalJobId, cancellationToken);
            if (job is null)
            {
                return OperationFailure.NotFound();
            }

            if (attempt.ManagerId != manager.ManagerId)
            {
                refusedRunId = job.RunId;
                refusedJobId = job.Id;
                refusalReason = AnalysisAuditEvents.ForeignAttemptReason;
                failure = OperationFailure.Forbidden();
            }
            else
            {
                var now = time.GetUtcNow();
                if (query.FencingToken is not { } token)
                {
                    return OperationFailure.Validation(new FieldViolation("fencingToken", "required"));
                }

                if (attempt.State != ExecutionAttemptState.Active
                    || attempt.LeaseExpiresAt <= now
                    || attempt.FencingToken != token
                    || job.CurrentAttemptId != attempt.Id)
                {
                    return OperationFailure.Conflict(AttemptObsoleteCode);
                }

                var pin = await jobs.LoadSegmentMediaAsync(job.Id, cancellationToken);
                if (pin is null)
                {
                    refusedRunId = job.RunId;
                    refusedJobId = job.Id;
                    refusalReason = AnalysisAuditEvents.OutOfScopeReason;
                    failure = OperationFailure.Conflict(NotApplicableCode);
                }
                else
                {
                    var identity = job.RecordingVersionId is { } recording
                        && Sha256Digest.TryParse(job.TimelineMappingDigest, out var mapping)
                        && Sha256Digest.TryParse(job.SegmentationPolicyDigest, out var policy)
                        && job.SegmentNumber is > 0 and var number
                            ? new MaterializedSegmentIdentity(recording, mapping, policy, number)
                            : null;
                    var lifetimeEnd = now + options.Value.GrantLifetime;
                    scoped = new Scoped(
                        job.RunId, job.Id, pin, identity,
                        lifetimeEnd <= attempt.LeaseExpiresAt ? lifetimeEnd : attempt.LeaseExpiresAt);
                }
            }
        }

        if (scoped is null)
        {
            await audit.RecordIndependentAsync(
                AnalysisAuditEvents.SegmentMediaReadRefused(query.AttemptId, refusedJobId, refusedRunId!.Value, manager.ManagerId, refusalReason!),
                cancellationToken);
            return failure!;
        }

        var pinned = scoped.Pin;
        var mediaKey = MediaKeyOf(pinned.ManifestReference);
        var manifestBytes = await ReadVerifiedManifestAsync(pinned, cancellationToken);
        var mediaAttested = manifestBytes is not null
            && await storage.GetObjectChecksumAsync(mediaKey, cancellationToken) is { } media
            && string.Equals(media.Digest.ToString(), pinned.MediaDigest, StringComparison.Ordinal)
            && media.ContentLength == pinned.MediaSizeBytes;
        if (!mediaAttested)
        {
            await InvalidateAsync(scoped, mediaKey, cancellationToken);
            return OperationFailure.Conflict(InvalidCode);
        }

        using var document = JsonDocument.Parse(manifestBytes!);
        var grants = new[]
        {
            Grant(MaterializeSegmentHandler.ManifestFileName, pinned.ManifestReference, scoped.GrantsExpireAt),
            Grant(MaterializeSegmentHandler.MediaFileName, mediaKey, scoped.GrantsExpireAt),
        };
        return new GetSegmentMediaResult(
            query.AttemptId, scoped.JobId, scoped.RunId, pinned.ManifestChecksum, document.RootElement.Clone(), grants);
    }

    private SegmentMediaGrant Grant(string name, string key, DateTimeOffset expiresAt)
    {
        var grant = storage.PresignGetObject(key, expiresAt);
        return new SegmentMediaGrant(name, grant.Url, grant.ExpiresAt);
    }

    private static string MediaKeyOf(string manifestKey) =>
        manifestKey[..^MaterializeSegmentHandler.ManifestFileName.Length] + MaterializeSegmentHandler.MediaFileName;

    private async Task<byte[]?> ReadVerifiedManifestAsync(SegmentMediaPin pin, CancellationToken cancellationToken)
    {
        if (!pin.ManifestReference.EndsWith(MaterializeSegmentHandler.ManifestFileName, StringComparison.Ordinal)
            || !Sha256Digest.TryParse(pin.ManifestChecksum, out var expected))
        {
            return null;
        }

        var read = await storage.ReadObjectAsync(pin.ManifestReference, options.Value.MaxManifestBytes, cancellationToken);
        return read?.Content is { } content && Sha256Digest.FromBytes(SHA256.HashData(content)).Equals(expected)
            ? content
            : null;
    }

    // The materialized segment fails and its objects are deleted so the next demand materializes it again.
    private async Task InvalidateAsync(Scoped scoped, string mediaKey, CancellationToken cancellationToken)
    {
        if (scoped.Identity is null)
        {
            return;
        }

        SegmentAssociation? invalidated;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var row = await segments.FindAsync(scoped.Identity, cancellationToken);
            invalidated = row is { State: MaterializedSegmentState.Available, Association: { } association }
                && association.MediaDigest == scoped.Pin.MediaDigest
                && association.ManifestObjectKey == scoped.Pin.ManifestReference
                    ? await segments.TryInvalidateAvailableAsync(row.Id, association.MediaDigest, InvalidCode, cancellationToken)
                    : null;
            await scope.CommitAsync(cancellationToken);
        }

        if (invalidated is not null)
        {
            await storage.DeleteObjectAsync(invalidated.MediaObjectKey, CancellationToken.None);
            await storage.DeleteObjectAsync(invalidated.ManifestObjectKey, CancellationToken.None);
        }
    }
}
