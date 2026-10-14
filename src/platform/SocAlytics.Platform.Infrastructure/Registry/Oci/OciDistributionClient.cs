using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.Images;

namespace SocAlytics.Platform.Infrastructure.Registry.Oci;

internal sealed record OciObject(byte[] Content, string? MediaType);

internal sealed class OciRefusalException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>A minimal, read-only OCI Distribution client: manifests and blobs by digest, with bearer or basic authentication.</summary>
internal sealed partial class OciDistributionClient(HttpClient http, IOptions<ImageRegistryOptions> options)
{
    public const long MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxConfigBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly string ManifestAccept = string.Join(", ",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.docker.distribution.manifest.list.v2+json");

    private readonly ConcurrentDictionary<string, CachedAuthorization> _authorizations = new(StringComparer.Ordinal);

    public Task<OciObject> GetManifestAsync(string repository, string digest, CancellationToken cancellationToken) =>
        GetAsync(repository, "manifests", digest, ManifestAccept, MaxManifestBytes, ImageImportRefusalCodes.ManifestInvalid, cancellationToken);

    public Task<OciObject> GetConfigBlobAsync(string repository, string digest, CancellationToken cancellationToken) =>
        GetAsync(repository, "blobs", digest, "*/*", MaxConfigBytes, ImageImportRefusalCodes.ManifestInvalid, cancellationToken);

    public Task<OciObject> GetBlobAsync(string repository, string digest, long maxBytes, string tooLargeCode, CancellationToken cancellationToken) =>
        GetAsync(repository, "blobs", digest, "*/*", maxBytes, tooLargeCode, cancellationToken);

    public static string DigestOf(ReadOnlySpan<byte> content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private async Task<OciObject> GetAsync(
        string repository, string kind, string digest, string accept, long maxBytes, string tooLargeCode, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var scheme = settings.UseHttp ? "http" : "https";
        var url = new Uri($"{scheme}://{settings.RegistryHost}/v2/{repository}/{kind}/{digest}");

        try
        {
            using var response = await SendAsync(repository, url, accept, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.NotFound:
                    throw new OciRefusalException(ImageImportRefusalCodes.ImageNotFound, $"The registry has no {kind.TrimEnd('s')} {digest}.");
                default:
                    throw new OciRefusalException(
                        ImageImportRefusalCodes.RegistryUnavailable,
                        $"The registry answered {(int)response.StatusCode} for {kind.TrimEnd('s')} {digest}.");
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                throw new OciRefusalException(tooLargeCode, $"The {kind.TrimEnd('s')} {digest} exceeds {maxBytes} bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new OciRefusalException(tooLargeCode, $"The {kind.TrimEnd('s')} {digest} exceeds {maxBytes} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            var content = buffer.ToArray();
            if (!string.Equals(DigestOf(content), digest, StringComparison.Ordinal))
            {
                throw new OciRefusalException(ImageImportRefusalCodes.DigestMismatch, $"The {kind.TrimEnd('s')} content does not match {digest}.");
            }

            return new OciObject(content, response.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException ex)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, $"The registry is unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, $"The registry request timed out: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string repository, Uri url, string accept, CancellationToken cancellationToken)
    {
        AuthenticationHeaderValue? authorization = null;
        if (_authorizations.TryGetValue(repository, out var cached) && cached.ExpiresAtTicks > Environment.TickCount64)
        {
            authorization = cached.Header;
        }

        var response = await SendFollowingRedirectsAsync(url, accept, authorization, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
        response.Dispose();
        if (challenge is null)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry requires authentication without offering a challenge.");
        }

        authorization = await AuthenticateAsync(repository, challenge, cancellationToken);
        response = await SendFollowingRedirectsAsync(url, accept, authorization, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry refused the pull credentials.");
        }

        return response;
    }

    // Redirects are followed here so that the Authorization header never leaves the registry host.
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        Uri url, string accept, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            if (authorization is not null && current.Authority == url.Authority)
            {
                request.Headers.Authorization = authorization;
            }

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= MaxRedirects)
            {
                throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry redirected without a usable location.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (current.Scheme != Uri.UriSchemeHttps && !options.Value.UseHttp)
            {
                throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry redirected to a non-HTTPS location.");
            }
        }
    }

    private async Task<AuthenticationHeaderValue> AuthenticateAsync(
        string repository, AuthenticationHeaderValue challenge, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var hasCredentials = !string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrWhiteSpace(settings.Token);
        var basic = hasCredentials
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Token}")))
            : null;

        if (string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            return basic ?? throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry requires credentials that are not configured.");
        }

        if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, $"The registry offered the unsupported authentication scheme {challenge.Scheme}.");
        }

        var parameters = ChallengeParameter().Matches(challenge.Parameter ?? string.Empty)
            .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value, StringComparer.Ordinal);
        if (!parameters.TryGetValue("realm", out var realm)
            || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)
            || (realmUri.Scheme != Uri.UriSchemeHttps && !(settings.UseHttp && realmUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry bearer challenge has no usable realm.");
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add("service=" + Uri.EscapeDataString(service));
        }

        query.Add("scope=" + Uri.EscapeDataString(parameters.GetValueOrDefault("scope") ?? $"repository:{repository}:pull"));
        var tokenUrl = new UriBuilder(realmUri) { Query = string.Join('&', query) }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        request.Headers.Authorization = basic;
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, $"The registry token endpoint answered {(int)response.StatusCode}.");
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
            : root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()
            : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new OciRefusalException(ImageImportRefusalCodes.RegistryUnavailable, "The registry token endpoint returned no token.");
        }

        var lifetime = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 60;
        var header = new AuthenticationHeaderValue("Bearer", token);
        _authorizations[repository] = new CachedAuthorization(header, Environment.TickCount64 + Math.Max(0, lifetime - 10) * 1000L);
        return header;
    }

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeParameter();

    private sealed record CachedAuthorization(AuthenticationHeaderValue Header, long ExpiresAtTicks);
}
