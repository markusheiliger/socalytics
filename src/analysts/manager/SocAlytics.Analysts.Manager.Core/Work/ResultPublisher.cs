using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

public enum ResultPublishEnd
{
    /// <summary>Manifest and artifacts are stored under the attempt's result location.</summary>
    Published,

    /// <summary>The Analyst output is unusable; nothing was uploaded and a failed completion is reported.</summary>
    Invalid,

    /// <summary>Grants or storage could not be used right now; a failed completion is reported.</summary>
    Unavailable,

    /// <summary>The attempt is obsolete, forbidden, or the registration is inactive; the attempt stops without a report.</summary>
    Stopped,
}

public sealed record ResultPublishResult(ResultPublishEnd End, string? ManifestReference = null, string? ManifestChecksum = null);

/// <summary>Stores the Analyst's manifest and artifacts through write grants; result bytes never pass through the platform API.</summary>
public sealed partial class ResultPublisher(
    PlatformClient platform, HttpClient storage, TimeProvider time, bool allowLoopbackHttp, ILogger<ResultPublisher>? logger = null)
{
    public const string ManifestName = "manifest.json";
    public const string StorageClientName = "result-storage";

    private const int MaxRenewals = 3;
    private const int MaxAttempts = 5;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logger ?? NullLogger<ResultPublisher>.Instance;

    public const string ResultFolder = "result";

    public static string DirectoryFor(Guid attemptId) => Path.Combine(UpstreamStager.DirectoryFor(attemptId), ResultFolder);

    public async Task<ResultPublishResult> PublishAsync(
        ActiveAttempt attempt, string directory, Func<PlatformSession?> sessions, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifestPath))
        {
            LogInvalid(attempt, "result-manifest-missing");
            return new(ResultPublishEnd.Invalid);
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (!ResultManifestSchema.IsValid(manifestBytes))
        {
            LogInvalid(attempt, "result-manifest-schema-invalid");
            return new(ResultPublishEnd.Invalid);
        }

        var objects = new List<(string Name, byte[] Bytes, ResultObjectDeclaration Declaration)>();
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var bytes = name == ManifestName ? manifestBytes : await File.ReadAllBytesAsync(path, cancellationToken);
            if (!ObjectName().IsMatch(name) || bytes.Length == 0)
            {
                LogInvalid(attempt, "result-object-invalid");
                return new(ResultPublishEnd.Invalid);
            }

            objects.Add((name, bytes, new ResultObjectDeclaration(name, bytes.Length, Digest(bytes))));
        }

        var declarations = objects.Select(o => o.Declaration).ToList();
        var renewals = 0;
        var pending = objects.ToList();
        ResultGrantsResponse? grants = null;
        while (pending.Count > 0)
        {
            if (grants is null)
            {
                var requested = await RequestGrantsAsync(attempt, declarations, sessions, cancellationToken);
                if (requested.End is { } end)
                {
                    return new(end);
                }

                grants = requested.Grants!;
            }

            var next = pending[0];
            var grant = grants.Grants.FirstOrDefault(g => g.Name == next.Name);
            if (grant is null)
            {
                _logger.LogWarning(
                    "Grants for attempt {AttemptId} do not cover object {ObjectName}", attempt.AttemptId, next.Name);
                return new(ResultPublishEnd.Unavailable);
            }

            var put = await PutAsync(attempt, grant, next.Bytes, cancellationToken);
            switch (put)
            {
                case PutOutcome.Stored:
                    pending.RemoveAt(0);
                    break;
                case PutOutcome.Expired when renewals < MaxRenewals:
                    renewals++;
                    grants = null;
                    break;
                default:
                    return new(ResultPublishEnd.Unavailable);
            }
        }

        var location = (grants?.ResultLocation) ?? string.Empty;
        _logger.LogInformation(
            "Stored {Count} result objects of attempt {AttemptId} for Logical Job {LogicalJobId}: {ObjectNames}",
            objects.Count, attempt.AttemptId, attempt.LogicalJobId, string.Join(',', objects.Select(o => o.Name)));
        var manifest = objects.First(o => o.Name == ManifestName);
        return new(ResultPublishEnd.Published, location + ManifestName, manifest.Declaration.Digest);
    }

    private async Task<(ResultGrantsResponse? Grants, ResultPublishEnd? End)> RequestGrantsAsync(
        ActiveAttempt attempt, IReadOnlyList<ResultObjectDeclaration> declarations, Func<PlatformSession?> sessions,
        CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;
        for (var tries = 1; ; tries++)
        {
            var session = sessions();
            if (session is null)
            {
                return (null, ResultPublishEnd.Stopped);
            }

            var result = await platform.RequestResultGrantsAsync(
                session, attempt.AttemptId, new ResultGrantsRequest(attempt.FencingToken, declarations), cancellationToken);
            switch (result.Kind)
            {
                case WorkResultKind.Success:
                case WorkResultKind.Replayed:
                    return (result.Value, null);
                case WorkResultKind.Transient when tries < MaxAttempts:
                    await Task.Delay(backoff, time, cancellationToken);
                    backoff *= 2;
                    continue;
                case WorkResultKind.Transient:
                    return (null, ResultPublishEnd.Unavailable);
                default:
                    _logger.LogWarning(
                        "Result grants for attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} were refused ({Kind} {Code}); stopping the attempt",
                        attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId, result.Kind, result.Code);
                    return (null, ResultPublishEnd.Stopped);
            }
        }
    }

    private enum PutOutcome
    {
        Stored,
        Expired,
        Failed,
    }

    private async Task<PutOutcome> PutAsync(ActiveAttempt attempt, ResultGrant grant, byte[] bytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(grant.Url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
        {
            _logger.LogWarning("Grant for object {ObjectName} of attempt {AttemptId} has a disallowed storage endpoint", grant.Name, attempt.AttemptId);
            return PutOutcome.Failed;
        }

        var backoff = InitialBackoff;
        for (var tries = 1; ; tries++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(grant.Method), uri) { Content = new ByteArrayContent(bytes) };
            foreach (var (header, value) in grant.RequiredHeaders)
            {
                if (header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(header, value))
                {
                    request.Content.Headers.TryAddWithoutValidation(header, value);
                }
            }

            try
            {
                using var response = await storage.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return PutOutcome.Stored;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogInformation(
                        "Storage refused object {ObjectName} of attempt {AttemptId} with 403; renewing the grants",
                        grant.Name, attempt.AttemptId);
                    return PutOutcome.Expired;
                }

                if ((int)response.StatusCode < 500)
                {
                    _logger.LogWarning(
                        "Storage rejected object {ObjectName} of attempt {AttemptId} with {Status}",
                        grant.Name, attempt.AttemptId, (int)response.StatusCode);
                    return PutOutcome.Failed;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (tries >= MaxAttempts)
            {
                return PutOutcome.Failed;
            }

            await Task.Delay(backoff, time, cancellationToken);
            backoff *= 2;
        }
    }

    private bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    private void LogInvalid(ActiveAttempt attempt, string reason) =>
        _logger.LogWarning(
            "Result of attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} is not uploaded: {Reason}",
            attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId, reason);

    public static string Digest(byte[] bytes) => "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex ObjectName();
}
