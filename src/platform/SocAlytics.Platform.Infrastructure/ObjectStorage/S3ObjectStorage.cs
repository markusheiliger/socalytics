using System.Globalization;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Infrastructure.ObjectStorage;

internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly bool _useHttp;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options)
    {
        var value = options.Value;
        _bucket = value.Bucket;
        _useHttp = value.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var config = new AmazonS3Config
        {
            ServiceURL = value.ServiceUrl,
            ForcePathStyle = value.ForcePathStyle,
            AuthenticationRegion = value.Region,
            UseHttp = _useHttp,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            LogResponse = false,
            LogMetrics = false,
        };
        _client = new AmazonS3Client(new BasicAWSCredentials(value.AccessKey, value.SecretKey), config);
    }

    public async Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(
        string objectKey, string contentType, CancellationToken cancellationToken)
    {
        var response = await CallAsync(() => _client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                ContentType = contentType,
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumType = ChecksumType.COMPOSITE,
            },
            cancellationToken));
        return new MultipartUploadReference(objectKey, response.UploadId);
    }

    public PartUploadGrant PresignUploadPart(
        MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt)
    {
        var checksum = partDigest.ToBase64();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = upload.ObjectKey,
            Verb = HttpVerb.PUT,
            PartNumber = partNumber,
            UploadId = upload.UploadId,
            Expires = expiresAt.UtcDateTime,
            Protocol = _useHttp ? Protocol.HTTP : Protocol.HTTPS,
        };
        request.Headers["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture);
        request.Headers["x-amz-checksum-sha256"] = checksum;

        string url;
        try
        {
            url = _client.GetPreSignedURL(request);
        }
        catch (Exception ex) when (ex is AmazonClientException)
        {
            throw new ObjectStorageUnavailableException(ex);
        }

        return new PartUploadGrant(
            partNumber,
            "PUT",
            new Uri(url),
            new Dictionary<string, string>
            {
                ["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture),
                ["x-amz-checksum-sha256"] = checksum,
            },
            expiresAt);
    }

    public async Task<IReadOnlyList<StoredPart>> ListPartsAsync(
        MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken)
    {
        var parts = new List<StoredPart>();
        int? marker = null;
        while (true)
        {
            var response = await CallAsync(() => _client.ListPartsAsync(
                new ListPartsRequest
                {
                    BucketName = _bucket,
                    Key = upload.ObjectKey,
                    UploadId = upload.UploadId,
                    PartNumberMarker = marker?.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken));

            foreach (var part in response.Parts ?? [])
            {
                parts.Add(new StoredPart(part.PartNumber ?? 0, part.ETag ?? string.Empty, part.Size ?? 0));
                if (maxParts is { } limit && parts.Count >= limit)
                {
                    return parts;
                }
            }

            if (response.IsTruncated != true || response.NextPartNumberMarker is null)
            {
                return parts;
            }

            marker = response.NextPartNumberMarker;
        }
    }

    public async Task CompleteMultipartUploadAsync(
        MultipartUploadReference upload,
        IReadOnlyList<CompletedPartEntry> parts,
        CompositeContentDigest contentDigest,
        long totalSizeBytes,
        CancellationToken cancellationToken)
    {
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = upload.ObjectKey,
            UploadId = upload.UploadId,
            ChecksumSHA256 = contentDigest.ToS3ChecksumValue(),
            ChecksumType = ChecksumType.COMPOSITE,
            MpuObjectSize = totalSizeBytes,
            PartETags = [.. parts.Select(p => new PartETag(p.PartNumber, p.ETag) { ChecksumSHA256 = p.Digest.ToBase64() })],
        };
        await CallAsync(() => _client.CompleteMultipartUploadAsync(request, cancellationToken));
    }

    public async Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await CallAsync(() => _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = objectKey, ChecksumMode = ChecksumMode.ENABLED },
                cancellationToken));
            return new CompositeIntegrityEvidence(
                response.ChecksumSHA256,
                response.ChecksumType?.Value,
                response.ContentLength,
                response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(() => _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = _bucket, Key = upload.ObjectKey, UploadId = upload.UploadId },
                cancellationToken));
        }
        catch (ObjectStorageRejectedException ex) when (ex.Reason == ObjectStorageRejectionReason.NoSuchUpload)
        {
        }
    }

    public async Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(() => _client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = _bucket, Key = objectKey },
                cancellationToken));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public void Dispose() => _client.Dispose();

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AmazonS3Exception ex)
        {
            throw Translate(ex);
        }
        catch (Exception ex) when (ex is AmazonClientException or HttpRequestException or TimeoutException
            or TaskCanceledException { InnerException: TimeoutException })
        {
            throw new ObjectStorageUnavailableException(ex);
        }
    }

    private static Exception Translate(AmazonS3Exception ex)
    {
        var reason = ex.ErrorCode switch
        {
            "InvalidPart" or "InvalidPartOrder" => ObjectStorageRejectionReason.InvalidPart,
            "BadDigest" or "InvalidDigest" or "XAmzContentSHA256Mismatch" => ObjectStorageRejectionReason.BadDigest,
            "EntityTooSmall" => ObjectStorageRejectionReason.EntityTooSmall,
            "NoSuchUpload" => ObjectStorageRejectionReason.NoSuchUpload,
            _ => (ObjectStorageRejectionReason?)null,
        };

        if (reason is { } mapped)
        {
            return new ObjectStorageRejectedException(mapped, ex);
        }

        return ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchKey"
            ? ex
            : (int)ex.StatusCode >= 500 || ex.StatusCode == 0
                ? new ObjectStorageUnavailableException(ex)
                : ex;
    }
}
