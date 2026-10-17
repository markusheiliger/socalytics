using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.RegistryCredentials;

namespace SocAlytics.Platform.Infrastructure.Registry.Oci;

/// <summary>Follows the registry's <c>WWW-Authenticate</c> challenge with the protected read credentials and returns a short-lived, pull-only token; the token is never logged.</summary>
internal sealed partial class OciPullTokenIssuer(HttpClient http, IOptions<ImageRegistryOptions> options, TimeProvider time)
    : IRegistryPullTokenIssuer
{
    private const int DefaultTokenSeconds = 60;

    public string RegistryHost => options.Value.RegistryHost;

    public string RepositoryPrefix => options.Value.RepositoryPrefix;

    public async Task<RegistryPullToken> IssueAsync(IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var cap = now + settings.PullCredentialLifetime;
        var scheme = settings.UseHttp ? "http" : "https";
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri($"{scheme}://{settings.RegistryHost}/v2/"));
            using var probeResponse = await http.SendAsync(probe, cancellationToken);
            if (probeResponse.StatusCode == HttpStatusCode.OK)
            {
                return new RegistryPullToken(RegistryCredentialKinds.Anonymous, null, cap);
            }

            var challenge = probeResponse.StatusCode == HttpStatusCode.Unauthorized
                ? probeResponse.Headers.WwwAuthenticate.FirstOrDefault()
                : null;
            if (challenge is null || !string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new RegistryPullTokenException($"The registry answered {(int)probeResponse.StatusCode} without a usable bearer challenge.");
            }

            return await RequestTokenAsync(settings, challenge, repositories, now, cap, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryPullTokenException($"The registry is unreachable: {ex.GetType().Name}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RegistryPullTokenException("The registry request timed out.");
        }
    }

    private async Task<RegistryPullToken> RequestTokenAsync(
        ImageRegistryOptions settings, AuthenticationHeaderValue challenge, IReadOnlyList<string> repositories,
        DateTimeOffset now, DateTimeOffset cap, CancellationToken cancellationToken)
    {
        var parameters = ChallengeParameter().Matches(challenge.Parameter ?? string.Empty)
            .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value, StringComparer.Ordinal);
        if (!parameters.TryGetValue("realm", out var realm)
            || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)
            || (realmUri.Scheme != Uri.UriSchemeHttps && !(settings.UseHttp && realmUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new RegistryPullTokenException("The registry bearer challenge has no usable realm.");
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add("service=" + Uri.EscapeDataString(service));
        }

        query.AddRange(repositories.Select(static r => "scope=" + Uri.EscapeDataString($"repository:{r}:pull")));
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(realmUri) { Query = string.Join('&', query) }.Uri);
        if (!string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrWhiteSpace(settings.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Token}")));
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RegistryPullTokenException($"The registry token endpoint answered {(int)response.StatusCode}.");
        }

        JsonElement root;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RegistryPullTokenException("The registry token endpoint returned an unreadable response.");
        }

        var token = root.ValueKind == JsonValueKind.Object
            ? root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
            : root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null
            : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new RegistryPullTokenException("The registry token endpoint returned no token.");
        }

        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) && s > 0 ? s : DefaultTokenSeconds;
        var expiresAt = now.AddSeconds(seconds);
        return new RegistryPullToken(RegistryCredentialKinds.Bearer, token, expiresAt < cap ? expiresAt : cap);
    }

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeParameter();
}
