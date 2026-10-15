using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

public enum StagingEnd
{
    /// <summary>Every upstream dependency is staged and verified.</summary>
    Staged,

    /// <summary>Staging failed; the Analyst must not be launched and a failed completion is reported.</summary>
    Failed,

    /// <summary>The read was refused as obsolete, or the registration is inactive; the attempt stops without a report.</summary>
    Obsolete,
}

/// <param name="Reason">Local reason (<see cref="StagingReasons"/>); never part of a completion.</param>
public sealed record StagingResult(StagingEnd End, string? Reason = null)
{
    /// <summary>The completion <c>failure.category</c> of the attempt-completion contract for a failed staging.</summary>
    public string? FailureCategory => Reason switch
    {
        StagingReasons.LimitExceeded or StagingReasons.DiskFull => "resource-exhausted",
        null => null,
        _ => "input-unavailable",
    };
}

public static class StagingReasons
{
    public const string DigestMismatch = "staging-digest-mismatch";
    public const string LimitExceeded = "staging-limit-exceeded";
    public const string DiskFull = "staging-disk-full";
    public const string StorageUnavailable = "staging-storage-unavailable";
    public const string ReadRefused = "staging-read-refused";
}

/// <summary>
/// Stages the accepted upstream results of a job into <c>{attemptDirectory}/inputs</c> (contracts/staged-inputs.md) through
/// the platform read and presigned grants; the Analyst never receives grants, tokens, or the platform address.
/// </summary>
public sealed partial class UpstreamStager(
    PlatformClient platform, HttpClient storage, TimeProvider time, StagingOptions options, bool allowLoopbackHttp,
    ILogger<UpstreamStager>? logger = null, Func<string, long>? freeSpace = null)
{
    public const string InputsFolder = "inputs";
    public const string ManifestName = "manifest.json";

    private const int MaxRenewals = 3;
    private const int PageGuard = 10_000;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger ?? NullLogger<UpstreamStager>.Instance;
    private readonly Func<string, long> _freeSpace = freeSpace ?? DefaultFreeSpace;

    public static string DirectoryFor(Guid attemptId) =>
        Path.Combine(Path.GetTempPath(), "socalytics-analyst-attempts", attemptId.ToString("N"));

    /// <summary>True when the job's snapshot lists accepted upstream results to stage.</summary>
    public static bool HasUpstream(AnalystJobMessage job) => Dependencies(job).Count > 0;

    public async Task<StagingResult> StageAsync(
        ActiveAttempt attempt, string attemptDirectory, Func<PlatformSession?> sessions, CancellationToken cancellationToken)
    {
        var inputs = Path.Combine(attemptDirectory, InputsFolder);
        var deadline = attempt.StartedAt + attempt.ExecutionTimeout;
        try
        {
            var result = await StageCoreAsync(attempt, inputs, deadline, sessions, cancellationToken);
            if (result.End != StagingEnd.Staged)
            {
                DeleteQuietly(inputs);
            }

            return result;
        }
        catch
        {
            DeleteQuietly(inputs);
            throw;
        }
    }

    private async Task<StagingResult> StageCoreAsync(
        ActiveAttempt attempt, string inputs, DateTimeOffset deadline, Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var dependencies = Dependencies(attempt.Job);
        var loaded = new List<Dependency>();
        foreach (var (capability, schemaVersion) in dependencies)
        {
            if (!SafeName().IsMatch(capability.Id))
            {
                return Failed(attempt, StagingReasons.ReadRefused, "unsafe capability id");
            }

            var read = await ReadAllAsync(attempt, capability, schemaVersion, deadline, sessions, ct);
            if (read.Result is { } failure)
            {
                return failure;
            }

            loaded.Add(read.Dependency!);
        }

        long total = 0;
        foreach (var result in loaded.SelectMany(d => d.Results))
        {
            if (!SafeName().IsMatch(result.AcceptedResultId))
            {
                return Failed(attempt, StagingReasons.ReadRefused, "unsafe accepted result id");
            }

            total += DeclaredBytes(result);
        }

        Directory.CreateDirectory(inputs);
        if (total > options.MaxBytesPerAttempt)
        {
            return Failed(attempt, StagingReasons.LimitExceeded, $"{total} bytes exceed the staging limit");
        }

        if (total > _freeSpace(inputs))
        {
            return Failed(attempt, StagingReasons.DiskFull, $"{total} bytes exceed the free space of the staging volume");
        }

        foreach (var dependency in loaded)
        {
            var directory = Path.Combine(inputs, dependency.Capability.Id);
            Directory.CreateDirectory(directory);
            foreach (var result in dependency.Results.ToList())
            {
                var outcome = await StageResultAsync(attempt, dependency, result, directory, deadline, sessions, ct);
                if (outcome is not null)
                {
                    return outcome;
                }
            }

            WriteCoverage(directory, dependency);
        }

        return new StagingResult(StagingEnd.Staged);
    }

    private async Task<StagingResult?> StageResultAsync(
        ActiveAttempt attempt, Dependency dependency, UpstreamResult result, string directory, DateTimeOffset deadline,
        Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var resultDirectory = Path.Combine(directory, result.AcceptedResultId);
        Directory.CreateDirectory(resultDirectory);
        var current = result;
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

                var (state, reason) = await DownloadAsync(
                    attempt, grant, Path.Combine(resultDirectory, name), expectedDigest, deadline, ct);
                switch (state)
                {
                    case Download.Stored:
                        return null;
                    case Download.Expired when renewals < MaxRenewals:
                        renewals++;
                        var renewed = await ReadAllAsync(
                            attempt, dependency.Capability, dependency.SchemaVersion, deadline, sessions, ct);
                        if (renewed.Result is { } stop)
                        {
                            return stop;
                        }

                        current = renewed.Dependency!.Results.FirstOrDefault(r => r.AcceptedResultId == result.AcceptedResultId)
                            ?? current;
                        continue;
                    case Download.Expired:
                        return Failed(attempt, StagingReasons.ReadRefused, "grants kept expiring");
                    default:
                        return Failed(attempt, reason!, name);
                }
            }
        }

        if (await Fetch(ManifestName, result.ManifestChecksum) is { } manifestFailure)
        {
            return manifestFailure;
        }

        foreach (var artifact in ReadArtifacts(Path.Combine(resultDirectory, ManifestName)))
        {
            if (artifact.Name == ManifestName)
            {
                continue;
            }

            if (!SafeName().IsMatch(artifact.Name))
            {
                return Failed(attempt, StagingReasons.DigestMismatch, "unsafe artifact name");
            }

            if (await Fetch(artifact.Name, artifact.Digest) is { } artifactFailure)
            {
                return artifactFailure;
            }
        }

        return null;
    }

    private enum Download
    {
        Stored,
        Expired,
        Failed,
    }

    private async Task<(Download State, string? Reason)> DownloadAsync(
        ActiveAttempt attempt, UpstreamGrant grant, string path, string expectedDigest, DateTimeOffset deadline, CancellationToken ct)
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
                        digest = await WriteAsync(body, path, ct);
                    }
                    catch (LocalWriteException)
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

    private static async Task<string> WriteAsync(Stream source, string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            try
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (IOException)
            {
                throw new LocalWriteException();
            }
        }

        return "sha-256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<(Dependency? Dependency, StagingResult? Result)> ReadAllAsync(
        ActiveAttempt attempt, QueueCapability capability, string schemaVersion, DateTimeOffset deadline,
        Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var results = new List<UpstreamResult>();
        var coverage = new List<UpstreamCoverageEntry>();
        UpstreamDescriptor? upstream = null;
        string? token = null;
        for (var page = 0; page < PageGuard; page++)
        {
            var read = await ReadPageAsync(attempt, capability, schemaVersion, token, deadline, sessions, ct);
            if (read.Result is { } failure)
            {
                return (null, failure);
            }

            var value = read.Page!;
            upstream ??= value.Upstream;
            results.AddRange(value.Results);
            foreach (var entry in value.Coverage)
            {
                if (!coverage.Any(c => c.SegmentId == entry.SegmentId && c.Match == entry.Match))
                {
                    coverage.Add(entry);
                }
            }

            token = value.ContinuationToken;
            if (token is null)
            {
                return (new Dependency(capability, schemaVersion, upstream, results, coverage), null);
            }
        }

        return (null, Failed(attempt, StagingReasons.ReadRefused, "too many pages"));
    }

    private async Task<(UpstreamResultsPage? Page, StagingResult? Result)> ReadPageAsync(
        ActiveAttempt attempt, QueueCapability capability, string schemaVersion, string? token, DateTimeOffset deadline,
        Func<PlatformSession?> sessions, CancellationToken ct)
    {
        var backoff = InitialBackoff;
        while (true)
        {
            var session = sessions();
            if (session is null)
            {
                return (null, new StagingResult(StagingEnd.Obsolete));
            }

            var read = await platform.ReadUpstreamResultsAsync(
                session, attempt.AttemptId, attempt.FencingToken, capability.Id, capability.Version, schemaVersion, token, ct);
            switch (read.Kind)
            {
                case WorkResultKind.Success:
                case WorkResultKind.Replayed:
                    return (read.Value, null);
                case WorkResultKind.Obsolete:
                case WorkResultKind.Inactive:
                    _logger.LogWarning(
                        "Upstream read of attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} is obsolete ({Kind}); stopping",
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

    private static void WriteCoverage(string directory, Dependency dependency)
    {
        var document = new
        {
            upstream = dependency.Upstream,
            coverage = dependency.Coverage,
        };
        File.WriteAllText(Path.Combine(directory, "coverage.json"), JsonSerializer.Serialize(document, PlatformJson.Options));
    }

    private static List<(string Name, string Digest)> ReadArtifacts(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var artifacts = new List<(string, string)>();
        if (document.RootElement.TryGetProperty("artifacts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in list.EnumerateArray())
            {
                artifacts.Add((artifact.GetProperty("name").GetString() ?? string.Empty, artifact.GetProperty("digest").GetString() ?? string.Empty));
            }
        }

        return artifacts;
    }

    private static long DeclaredBytes(UpstreamResult result)
    {
        long bytes = System.Text.Encoding.UTF8.GetByteCount(result.Manifest.GetRawText());
        if (result.Manifest.TryGetProperty("artifacts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in list.EnumerateArray())
            {
                if (artifact.TryGetProperty("sizeBytes", out var size) && size.TryGetInt64(out var n))
                {
                    bytes += n;
                }
            }
        }

        return bytes;
    }

    // The snapshot's accepted results carry the schema version the platform read requires.
    private static List<(QueueCapability Capability, string SchemaVersion)> Dependencies(AnalystJobMessage job)
    {
        var dependencies = new List<(QueueCapability, string)>();
        foreach (var input in job.Inputs ?? [])
        {
            var first = input.AcceptedResults?.FirstOrDefault();
            var capability = input.Capability ?? first?.Capability;
            if (first is null || capability is null || first.SchemaVersion is null
                || dependencies.Any(d => d.Item1.Id == capability.Id && d.Item1.Version == capability.Version))
            {
                continue;
            }

            dependencies.Add((capability, first.SchemaVersion));
        }

        return dependencies;
    }

    private StagingResult Failed(ActiveAttempt attempt, string reason, string detail)
    {
        _logger.LogWarning(
            "Staging for attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} failed: {Reason} ({Detail})",
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
            _logger.LogWarning("Removing partial staged inputs failed: {Error}", e.GetType().Name);
        }
    }

    private static long DefaultFreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private sealed class LocalWriteException : Exception;

    private sealed record Dependency(
        QueueCapability Capability, string SchemaVersion, UpstreamDescriptor Upstream, List<UpstreamResult> Results,
        List<UpstreamCoverageEntry> Coverage);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();
}
