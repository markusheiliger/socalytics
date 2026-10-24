using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>
/// Stages the materialized segment media of a job with <c>segmentMedia</c> into <c>{attemptDirectory}/media</c>
/// (contracts/segment-media-read.md) through the platform read and presigned grants; the Analyst never receives grants,
/// tokens, or the platform address. Failures use the staging reasons of <see cref="UpstreamStager"/>.
/// </summary>
public sealed class SegmentMediaStager(
    PlatformClient platform, HttpClient storage, TimeProvider time, StagingOptions options, bool allowLoopbackHttp,
    ILogger<SegmentMediaStager>? logger = null, Func<string, long>? freeSpace = null)
{
    public const string MediaFolder = "media";
    public const string ManifestName = "segment-manifest.json";
    public const string MediaName = "segment.mp4";

    private const int MaxRenewals = 3;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger ?? NullLogger<SegmentMediaStager>.Instance;
    private readonly Func<string, long> _freeSpace = freeSpace ?? (_ => long.MaxValue);

    /// <summary>True when the job pins materialized segment media to stage.</summary>
    public static bool HasMedia(AnalystJobMessage job) => job.SegmentMedia is not null;

    public async Task<StagingResult> StageAsync(
        ActiveAttempt attempt, string attemptDirectory, Func<PlatformSession?> sessions, CancellationToken cancellationToken)
    {
        var pinned = attempt.Job.SegmentMedia;
        if (pinned is null)
        {
            return new StagingResult(StagingEnd.Staged);
        }

        var media = Path.Combine(attemptDirectory, MediaFolder);
        var deadline = attempt.StartedAt + attempt.ExecutionTimeout;
        try
        {
            var result = await StageCoreAsync(attempt, pinned, attemptDirectory, media, deadline, sessions, cancellationToken);
            if (result.End != StagingEnd.Staged)
            {
                DeleteQuietly(media);
            }

            return result;
        }
        catch
        {
            DeleteQuietly(media);
            throw;
        }
    }

    private async Task<StagingResult> StageCoreAsync(
        ActiveAttempt attempt, JobSegmentMedia pinned, string attemptDirectory, string media, DateTimeOffset deadline,
        Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var read = await ReadAsync(attempt, deadline, sessions, ct);
        if (read.Result is { } failure)
        {
            return failure;
        }

        var current = read.Response!;
        if (current.ManifestChecksum != pinned.ManifestChecksum)
        {
            return Failed(attempt, StagingReasons.DigestMismatch, "response checksum differs from the pinned checksum");
        }

        Directory.CreateDirectory(media);
        var renewals = 0;

        async Task<StagingResult?> Fetch(string name, string expectedDigest)
        {
            while (true)
            {
                var grant = current.Grants.FirstOrDefault(g => g.Name == name);
                if (grant is null)
                {
                    return Failed(attempt, StagingReasons.ReadRefused, $"no grant for {name}");
                }

                var (state, reason) = await DownloadAsync(attempt, grant, Path.Combine(media, name), expectedDigest, deadline, ct);
                switch (state)
                {
                    case Download.Stored:
                        return null;
                    case Download.Expired when renewals < MaxRenewals:
                        renewals++;
                        var renewed = await ReadAsync(attempt, deadline, sessions, ct);
                        if (renewed.Result is { } stop)
                        {
                            return stop;
                        }

                        current = renewed.Response!;
                        continue;
                    case Download.Expired:
                        return Failed(attempt, StagingReasons.ReadRefused, "grants kept expiring");
                    default:
                        return Failed(attempt, reason!, name);
                }
            }
        }

        // The staged manifest, not the response copy, decides what the media must be.
        if (await Fetch(ManifestName, pinned.ManifestChecksum) is { } manifestFailure)
        {
            return manifestFailure;
        }

        var manifestPath = Path.Combine(media, ManifestName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        if (!SegmentMediaSchema.IsManifestValid(manifestBytes))
        {
            return Failed(attempt, StagingReasons.DigestMismatch, "the manifest is not valid against the segment manifest schema");
        }

        string digest;
        long size;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            var mediaObject = document.RootElement.GetProperty("media");
            digest = mediaObject.GetProperty("digest").GetString()!;
            size = mediaObject.GetProperty("sizeBytes").GetInt64();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Failed(attempt, StagingReasons.DigestMismatch, "the manifest media description is unreadable");
        }

        var total = size + manifestBytes.Length + DirectorySize(Path.Combine(attemptDirectory, UpstreamStager.InputsFolder));
        if (total > options.MaxBytesPerAttempt)
        {
            return Failed(attempt, StagingReasons.LimitExceeded, $"{total} bytes exceed the staging limit");
        }

        if (size > _freeSpace(media))
        {
            return Failed(attempt, StagingReasons.DiskFull, $"{size} bytes exceed the free space of the staging volume");
        }

        if (await Fetch(MediaName, digest) is { } mediaFailure)
        {
            return mediaFailure;
        }

        return new FileInfo(Path.Combine(media, MediaName)).Length == size
            ? new StagingResult(StagingEnd.Staged)
            : Failed(attempt, StagingReasons.DigestMismatch, "media size differs from the manifest");
    }

    private enum Download
    {
        Stored,
        Expired,
        Failed,
    }

    private async Task<(Download State, string? Reason)> DownloadAsync(
        ActiveAttempt attempt, SegmentMediaGrant grant, string path, string expectedDigest, DateTimeOffset deadline, CancellationToken ct)
    {
        if (!Uri.TryCreate(grant.Url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
        {
            _logger.LogWarning("Read grant for {ObjectName} of attempt {AttemptId} has a disallowed storage endpoint", grant.Name, attempt.AttemptId);
            return (Download.Failed, StagingReasons.ReadRefused);
        }

        var backoff = InitialBackoff;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await storage.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return (Download.Expired, null);
                }

                if (response.IsSuccessStatusCode)
                {
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    string digest;
                    try
                    {
                        digest = await UpstreamStager.WriteAsync(body, path, ct);
                    }
                    catch (UpstreamStager.LocalWriteException)
                    {
                        return (Download.Failed, StagingReasons.DiskFull);
                    }

                    return digest == expectedDigest ? (Download.Stored, null) : (Download.Failed, StagingReasons.DigestMismatch);
                }

                if ((int)response.StatusCode < 500)
                {
                    _logger.LogWarning(
                        "Storage rejected read of {ObjectName} of attempt {AttemptId} with {Status}",
                        grant.Name, attempt.AttemptId, (int)response.StatusCode);
                    return (Download.Failed, StagingReasons.ReadRefused);
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
            }

            if (time.GetUtcNow() + backoff >= deadline)
            {
                return (Download.Failed, StagingReasons.StorageUnavailable);
            }

            await Task.Delay(backoff, time, ct);
            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }

    private async Task<(SegmentMediaResponse? Response, StagingResult? Result)> ReadAsync(
        ActiveAttempt attempt, DateTimeOffset deadline, Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var backoff = InitialBackoff;
        while (true)
        {
            var session = sessions();
            if (session is null)
            {
                return (null, new StagingResult(StagingEnd.Obsolete));
            }

            var read = await platform.GetSegmentMediaAsync(session, attempt.AttemptId, attempt.FencingToken, ct);
            switch (read.Kind)
            {
                case WorkResultKind.Success:
                case WorkResultKind.Replayed:
                    return (read.Value, null);
                case WorkResultKind.Obsolete:
                case WorkResultKind.Inactive:
                    _logger.LogWarning(
                        "Segment media read of attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} is obsolete ({Kind}); stopping",
                        attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId, read.Kind);
                    return (null, new StagingResult(StagingEnd.Obsolete));
                case WorkResultKind.Transient:
                    if (time.GetUtcNow() + backoff >= deadline)
                    {
                        return (null, Failed(attempt, StagingReasons.StorageUnavailable, "platform unavailable"));
                    }

                    await Task.Delay(backoff, time, ct);
                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                    continue;
                default:
                    return (null, Failed(attempt, StagingReasons.ReadRefused, read.Code ?? read.Kind.ToString()));
            }
        }
    }

    private static long DirectorySize(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    private StagingResult Failed(ActiveAttempt attempt, string reason, string detail)
    {
        _logger.LogWarning(
            "Segment media staging for attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} failed: {Reason} ({Detail})",
            attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId, reason, detail);
        return new StagingResult(StagingEnd.Failed, reason);
    }

    private bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    private void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Removing partial staged media failed: {Error}", e.GetType().Name);
        }
    }
}
