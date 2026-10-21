using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

public enum SegmentMaterializationResult
{
    Published,

    // The candidate no longer owned the row (reclaimed or purged); its objects were deleted.
    Superseded,

    RetryScheduled,

    Failed,
}

public sealed record SegmentMaterializationOutcome(
    Guid SegmentId,
    SegmentMaterializationResult Result,
    SegmentAssociation? Association,
    string? FailureReason);

// Claims, extracts, stores, verifies, and publishes segments. Each state change runs in its own unit of work; extraction
// and storage never run inside one.
public sealed class MaterializeSegmentHandler(
    IUnitOfWork unitOfWork,
    ISegmentStore store,
    ISegmentSourceLookup sources,
    ISegmentExtractor extractor,
    IObjectStorage storage,
    SegmentManifestBuilder manifestBuilder,
    IOptions<SegmentMaterializationOptions> options,
    TimeProvider time,
    ILogger<MaterializeSegmentHandler> logger,
    IAnalysisRunStore? runs = null,
    RunReadinessCoordinator? readiness = null)
{
    public const string MediaFileName = "segment.mp4";
    public const string ManifestFileName = "segment-manifest.json";

    private static readonly TimeSpan SourceUrlMargin = TimeSpan.FromMinutes(1);

    public static string CandidatePrefix(MaterializedSegmentIdentity identity, Guid candidateId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"segments/{identity.RecordingVersionId:D}/{HexOf(identity.TimelineMappingDigest)}/{HexOf(identity.SegmentationPolicyDigest)}/{identity.SegmentNumber}/{candidateId:D}/");

    private static string HexOf(Sha256Digest digest) => digest.ToString()[Sha256Digest.Prefix.Length..];

    // Claims due rows in one unit of work and processes them one after another.
    public async Task<IReadOnlyList<SegmentMaterializationOutcome>> ProcessDueAsync(
        string leaseOwner, int limit, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        IReadOnlyList<ClaimedSegment> claimed;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            claimed = await store.ClaimDueAsync(leaseOwner, settings.LeaseDuration, limit, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        var outcomes = new List<SegmentMaterializationOutcome>(claimed.Count);
        foreach (var claim in claimed)
        {
            outcomes.Add(await ProcessAsync(claim, cancellationToken));
        }

        return outcomes;
    }

    public async Task<SegmentMaterializationOutcome> ProcessAsync(ClaimedSegment claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var segment = claim.Segment;
        var identity = segment.Identity;
        var candidateId = segment.CandidateId
            ?? throw new InvalidOperationException("A claimed segment has a candidate.");

        if (claim.ReclaimedCandidateId is { } expired)
        {
            await DeleteCandidateObjectsAsync(identity, expired, CancellationToken.None);
        }

        try
        {
            return await MaterializeAsync(segment, identity, candidateId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The lease expires and a later worker reclaims the candidate.
            await DeleteCandidateObjectsAsync(identity, candidateId, CancellationToken.None);
            throw;
        }
    }

    // A published association is a cache hit only when the store still attests its checksums; otherwise the row moves
    // to failed with segment-media-invalid and its objects are deleted.
    public async Task<SegmentAssociation?> VerifyAvailableAsync(
        MaterializedSegmentRecord segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.State != MaterializedSegmentState.Available || segment.Association is not { } association)
        {
            return null;
        }

        if (await AttestsAsync(association, cancellationToken))
        {
            return association;
        }

        SegmentAssociation? invalidated;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            invalidated = await store.TryInvalidateAvailableAsync(
                segment.Id, association.MediaDigest, SegmentFailureReasons.MediaInvalid, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        if (invalidated is not null)
        {
            await storage.DeleteObjectAsync(invalidated.MediaObjectKey, CancellationToken.None);
            await storage.DeleteObjectAsync(invalidated.ManifestObjectKey, CancellationToken.None);
        }

        return null;
    }

    private async Task<bool> AttestsAsync(SegmentAssociation association, CancellationToken cancellationToken)
    {
        var media = await storage.GetObjectChecksumAsync(association.MediaObjectKey, cancellationToken);
        var manifest = await storage.GetObjectChecksumAsync(association.ManifestObjectKey, cancellationToken);
        return media is not null
            && manifest is not null
            && string.Equals(media.Digest.ToString(), association.MediaDigest, StringComparison.Ordinal)
            && media.ContentLength == association.MediaSizeBytes
            && string.Equals(manifest.Digest.ToString(), association.ManifestChecksum, StringComparison.Ordinal);
    }

    private async Task<SegmentMaterializationOutcome> MaterializeAsync(
        MaterializedSegmentRecord segment, MaterializedSegmentIdentity identity, Guid candidateId, CancellationToken cancellationToken)
    {
        var source = await sources.FindAsync(identity.RecordingVersionId, identity.TimelineMappingDigest.ToString(), cancellationToken);
        if (source is null)
        {
            return await FailAsync(segment, candidateId, SegmentFailureReasons.ScopeMismatch, cancellationToken);
        }

        var window = SegmentWindowCalculator.Calculate(identity.SegmentNumber, segment.DurationSeconds, source.Spans);
        if (window.IsRejected)
        {
            return await FailAsync(segment, candidateId, window.RejectionReason!, cancellationToken);
        }

        var settings = options.Value;
        var readUrl = storage.PresignGetObject(
            source.SourceObjectKey, time.GetUtcNow() + settings.ExecutionTimeout + SourceUrlMargin).Url;

        ExtractedSegment extracted;
        try
        {
            extracted = await extractor.ExtractAsync(readUrl, window.Window!.RequestedSourceInterval, cancellationToken);
        }
        catch (SegmentExtractionException ex)
        {
            logger.LogWarning("Segment {SegmentId} extraction failed: {Reason}.", segment.Id, ex.Reason);
            return ex.Transient
                ? await RetryOrExhaustAsync(segment, candidateId, ex.Reason, cancellationToken)
                : await FailAsync(segment, candidateId, ex.Reason, cancellationToken);
        }

        await using (extracted)
        {
            var prefix = CandidatePrefix(identity, candidateId);
            var mediaKey = prefix + MediaFileName;
            var manifestKey = prefix + ManifestFileName;

            try
            {
                Sha256Digest mediaDigest;
                await using (var hashStream = File.OpenRead(extracted.MediaFilePath))
                {
                    mediaDigest = Sha256Digest.FromBytes(await SHA256.HashDataAsync(hashStream, cancellationToken));
                }

                await using (var content = File.OpenRead(extracted.MediaFilePath))
                {
                    await storage.PutObjectAsync(mediaKey, SegmentManifestBuilder.MediaType, content, extracted.SizeBytes, mediaDigest, cancellationToken);
                }

                var manifest = manifestBuilder.Build(new SegmentManifestInput(
                    identity,
                    segment.PolicyVersion,
                    segment.DurationSeconds,
                    window.Window,
                    extracted.EncodedInterval,
                    MediaFileName,
                    mediaDigest,
                    extracted.SizeBytes,
                    extracted.Toolchain,
                    time.GetUtcNow()));
                using (var content = new MemoryStream(manifest.CanonicalBytes, writable: false))
                {
                    await storage.PutObjectAsync(
                        manifestKey, "application/json", content, manifest.CanonicalBytes.Length, manifest.Checksum, cancellationToken);
                }

                var association = new SegmentAssociation(
                    mediaKey, manifestKey, mediaDigest.ToString(), extracted.SizeBytes, manifest.Checksum.ToString());
                if (!await AttestsAsync(association, cancellationToken))
                {
                    await DeleteCandidateObjectsAsync(identity, candidateId, CancellationToken.None);
                    return await FailAsync(segment, candidateId, SegmentFailureReasons.MediaInvalid, cancellationToken);
                }

                bool published;
                await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
                {
                    published = await store.TryPublishAsync(segment.Id, candidateId, association, cancellationToken);
                    if (published && runs is not null && readiness is not null)
                    {
                        // Waiting jobs are re-evaluated in the publishing unit of work, run by run in id order.
                        var waiting = await store.ListWaitingJobsAsync(identity, cancellationToken);
                        foreach (var runId in waiting.Select(w => w.RunId).Distinct().Order())
                        {
                            await runs.LockRunAsync(runId, cancellationToken);
                            await readiness.ApplyAsync(runId, cancellationToken);
                        }
                    }

                    await scope.CommitAsync(cancellationToken);
                }

                if (published)
                {
                    return new SegmentMaterializationOutcome(segment.Id, SegmentMaterializationResult.Published, association, null);
                }

                await DeleteCandidateObjectsAsync(identity, candidateId, CancellationToken.None);
                return new SegmentMaterializationOutcome(segment.Id, SegmentMaterializationResult.Superseded, null, null);
            }
            catch (ObjectStorageRejectedException)
            {
                await DeleteCandidateObjectsAsync(identity, candidateId, CancellationToken.None);
                return await FailAsync(segment, candidateId, SegmentFailureReasons.MediaInvalid, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Segment {SegmentId} storage failed.", segment.Id);
                await DeleteCandidateObjectsAsync(identity, candidateId, CancellationToken.None);
                return await RetryOrExhaustAsync(segment, candidateId, SegmentFailureReasons.StorageUnavailable, cancellationToken);
            }
        }
    }

    private async Task<SegmentMaterializationOutcome> RetryOrExhaustAsync(
        MaterializedSegmentRecord segment, Guid candidateId, string reason, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (segment.AttemptsUsed >= settings.MaxAttempts)
        {
            return await FailAsync(segment, candidateId, SegmentFailureReasons.RetryBudgetExhausted, cancellationToken);
        }

        bool recorded;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            recorded = await store.TryRecordTransientFailureAsync(
                segment.Id, candidateId, time.GetUtcNow() + settings.RetryDelay, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new SegmentMaterializationOutcome(
            segment.Id,
            recorded ? SegmentMaterializationResult.RetryScheduled : SegmentMaterializationResult.Superseded,
            null,
            recorded ? reason : null);
    }

    private async Task<SegmentMaterializationOutcome> FailAsync(
        MaterializedSegmentRecord segment, Guid candidateId, string reason, CancellationToken cancellationToken)
    {
        bool failed;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            failed = await store.TryFailAsync(segment.Id, candidateId, reason, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new SegmentMaterializationOutcome(
            segment.Id,
            failed ? SegmentMaterializationResult.Failed : SegmentMaterializationResult.Superseded,
            null,
            failed ? reason : null);
    }

    private async Task DeleteCandidateObjectsAsync(
        MaterializedSegmentIdentity identity, Guid candidateId, CancellationToken cancellationToken)
    {
        var prefix = CandidatePrefix(identity, candidateId);
        try
        {
            await storage.DeleteObjectAsync(prefix + MediaFileName, cancellationToken);
            await storage.DeleteObjectAsync(prefix + ManifestFileName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleting the objects of segment candidate {CandidateId} failed.", candidateId);
        }
    }
}
