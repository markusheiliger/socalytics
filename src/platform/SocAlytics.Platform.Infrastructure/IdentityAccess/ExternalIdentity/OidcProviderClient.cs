using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity;

/// <summary>
/// OpenID Connect authorization code client per configured provider. Tokens, codes, and secrets are never logged;
/// only the provider identifier and a failure category are.
/// </summary>
internal sealed class OidcProviderClient : IOidcProviderClient
{
    private static readonly string[] AllowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly IOptions<IdentityAccessOptions> _options;
    private readonly HttpClient _http;
    private readonly ILogger<OidcProviderClient> _logger;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastReset = new(StringComparer.Ordinal);
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public OidcProviderClient(IOptions<IdentityAccessOptions> options, HttpClient http, ILogger<OidcProviderClient> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
    }

    public async Task<Uri> BuildAuthorizationUrlAsync(
        string providerId, string redirectUri, string state, string nonce, string codeVerifier, CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerId);
        var configuration = await GetConfigurationAsync(providerId, cancellationToken);
        if (!Uri.TryCreate(configuration.AuthorizationEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw Fail(providerId, OidcProviderFailure.Unavailable);
        }

        var parameters = new (string Name, string Value)[]
        {
            ("response_type", "code"),
            ("client_id", provider.ClientId),
            ("redirect_uri", redirectUri),
            ("scope", Scope(provider)),
            ("state", state),
            ("nonce", nonce),
            ("code_challenge", Challenge(codeVerifier)),
            ("code_challenge_method", "S256"),
        };
        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));
        var builder = new UriBuilder(endpoint);
        builder.Query = string.IsNullOrEmpty(endpoint.Query) ? query : endpoint.Query.TrimStart('?') + "&" + query;
        return builder.Uri;
    }

    public async Task<OidcIdentity> ExchangeCodeAsync(
        string providerId, string redirectUri, string code, string codeVerifier, string expectedNonce, CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerId);
        var configuration = await GetConfigurationAsync(providerId, cancellationToken);
        var idToken = await RequestIdTokenAsync(providerId, provider, configuration, redirectUri, code, codeVerifier, cancellationToken);
        return await ValidateIdTokenAsync(providerId, provider, idToken, expectedNonce, cancellationToken);
    }

    private async Task<string> RequestIdTokenAsync(
        string providerId, IdentityAccessOptions.ExternalProviderOptions provider, OpenIdConnectConfiguration configuration,
        string redirectUri, string code, string codeVerifier, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(configuration.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint))
        {
            throw Fail(providerId, OidcProviderFailure.Unavailable);
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        if (provider.TokenEndpointAuthMethod == IdentityAccessOptions.ExternalProviderOptions.PostAuthMethod)
        {
            form["client_id"] = provider.ClientId;
            form["client_secret"] = provider.ClientSecret;
        }
        else
        {
            var credentials = $"{Uri.EscapeDataString(provider.ClientId)}:{Uri.EscapeDataString(provider.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Fail(providerId, response.StatusCode >= System.Net.HttpStatusCode.InternalServerError
                    ? OidcProviderFailure.Unavailable
                    : OidcProviderFailure.Rejected);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(token.GetString()))
            {
                return token.GetString()!;
            }

            throw Fail(providerId, OidcProviderFailure.InvalidToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Fail(providerId, OidcProviderFailure.Unavailable);
        }
        catch (JsonException)
        {
            throw Fail(providerId, OidcProviderFailure.InvalidToken);
        }
    }

    private async Task<OidcIdentity> ValidateIdTokenAsync(
        string providerId, IdentityAccessOptions.ExternalProviderOptions provider, string idToken, string expectedNonce,
        CancellationToken cancellationToken)
    {
        var configuration = await GetConfigurationAsync(providerId, cancellationToken);
        var result = await ValidateAsync(provider, configuration, idToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException)
        {
            // An unknown signing key usually means the provider rotated its keys.
            // RequestRefresh refreshes in the background, so a rate-limited fresh manager is used instead.
            if (TryResetManager(providerId, provider))
            {
                configuration = await GetConfigurationAsync(providerId, cancellationToken);
                result = await ValidateAsync(provider, configuration, idToken);
            }
        }

        if (!result.IsValid)
        {
            throw Fail(providerId, OidcProviderFailure.InvalidToken);
        }

        var claims = result.Claims;
        if (!claims.TryGetValue("nonce", out var nonce) || nonce is not string nonceValue || !FixedEquals(nonceValue, expectedNonce)
            || !claims.TryGetValue("sub", out var sub) || sub is not string subject || subject.Length == 0
            || subject.Length > 255 || string.IsNullOrEmpty(configuration.Issuer))
        {
            throw Fail(providerId, OidcProviderFailure.InvalidToken);
        }

        return new OidcIdentity(configuration.Issuer, subject);
    }

    private async Task<TokenValidationResult> ValidateAsync(
        IdentityAccessOptions.ExternalProviderOptions provider, OpenIdConnectConfiguration configuration, string idToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
            ValidateAudience = true,
            ValidAudience = provider.ClientId,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = AllowedAlgorithms,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        return await _tokenHandler.ValidateTokenAsync(idToken, parameters);
    }

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerId);
        var manager = _managers.GetOrAdd(providerId, _ => CreateManager(provider));
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await manager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Fail(providerId, OidcProviderFailure.Unavailable);
        }

        // The discovery document must name the configured authority as its issuer.
        if (!string.Equals(configuration.Issuer?.TrimEnd('/'), provider.Authority.TrimEnd('/'), StringComparison.Ordinal))
        {
            throw Fail(providerId, OidcProviderFailure.Unavailable);
        }

        return configuration;
    }

    private bool TryResetManager(string providerId, IdentityAccessOptions.ExternalProviderOptions provider)
    {
        var now = Environment.TickCount64;
        var last = _lastReset.GetOrAdd(providerId, _ => long.MinValue / 2);
        if (now - last < 30_000 || !_lastReset.TryUpdate(providerId, now, last))
        {
            return false;
        }

        _managers[providerId] = CreateManager(provider);
        return true;
    }

    private ConfigurationManager<OpenIdConnectConfiguration> CreateManager(IdentityAccessOptions.ExternalProviderOptions provider)
    {
        var metadata = provider.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        // The scheme was validated at startup (HTTPS outside Development and Test).
        var retriever = new HttpDocumentRetriever(_http) { RequireHttps = false };
        return new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever(), retriever)
        {
            RefreshInterval = TimeSpan.FromSeconds(30),
        };
    }

    private IdentityAccessOptions.ExternalProviderOptions GetProvider(string providerId) =>
        _options.Value.ExternalProviders.TryGetValue(providerId, out var provider)
            ? provider
            : throw new OidcProviderException(OidcProviderFailure.UnknownProvider);

    private static string Scope(IdentityAccessOptions.ExternalProviderOptions provider)
    {
        var scopes = provider.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes.Insert(0, "openid");
        }

        return string.Join(' ', scopes);
    }

    private static string Challenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private OidcProviderException Fail(string providerId, OidcProviderFailure failure)
    {
        _logger.LogWarning("External identity provider {ProviderId} interaction failed: {Failure}.", providerId, failure);
        return new OidcProviderException(failure);
    }
}
