using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

public interface IObjectStorage
{
    Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(
        string objectKey, string contentType, CancellationToken cancellationToken);

    PartUploadGrant PresignUploadPart(
        MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt);

    /// <summary>Lists all pages of stored parts, or at most <paramref name="maxParts"/> when given.</summary>
    Task<IReadOnlyList<StoredPart>> ListPartsAsync(
        MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken);

    Task CompleteMultipartUploadAsync(
        MultipartUploadReference upload,
        IReadOnlyList<CompletedPartEntry> parts,
        CompositeContentDigest contentDigest,
        long totalSizeBytes,
        CancellationToken cancellationToken);

    Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken);

    ObjectUploadGrant PresignPutObject(string objectKey, long contentLength, Sha256Digest digest, DateTimeOffset expiresAt);

    /// <summary>The full-object SHA-256 and length, or null when the object is missing or the store cannot attest a full-object checksum.</summary>
    Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes without reading further; null when the object is missing.</summary>
    Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken);

    /// <summary>An already missing upload (<c>NoSuchUpload</c>) is success.</summary>
    Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken);

    /// <summary>An already missing object is success.</summary>
    Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken);
}
