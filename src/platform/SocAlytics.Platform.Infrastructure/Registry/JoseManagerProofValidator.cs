using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class JoseManagerProofValidator(
    IProofReplayStore replayStore,
    IOptions<RegistryOptions> registry,
    IOptions<StampOptions> stamp,
    TimeProvider timeProvider) : IManagerProofValidator
{
    private static readonly JsonWebTokenHandler Handler = new();
    private static readonly ProofValidationResult Invalid = ProofValidationResult.Refused(ProofRefusalReasons.Invalid);

    public async Task<ProofValidationResult> ValidateActivationProofAsync(
        string proof, string storedPublicJwk, CancellationToken cancellationToken)
    {
        var jwt = await ParseAndVerifyAsync(proof, ParseKey(storedPublicJwk), "analyst-manager-activation+jwt");
        if (jwt is null || HasHeader(jwt, "jwk")
            || !SingleString(jwt, "aud", out var aud) || aud != stamp.Value.ActivationUri.ToString()
            || !SingleString(jwt, "challenge", out var challenge) || string.IsNullOrEmpty(challenge)
            || !WithinSkew(jwt) || !Jti(jwt, out var jti))
        {
            return Invalid;
        }

        var skew = registry.Value.Tokens.ProofClockSkew;
        var replay = await RecordAsync(ProofKinds.Activation, jti!, timeProvider.GetUtcNow() + skew + skew, cancellationToken);
        return replay ?? new ProofValidationResult(true, null, challenge);
    }

    public async Task<ProofValidationResult> ValidateClientAssertionAsync(
        string assertion, string expectedClientId, string storedPublicJwk, CancellationToken cancellationToken,
        Uri? audience = null)
    {
        var jwt = await ParseAndVerifyAsync(assertion, ParseKey(storedPublicJwk), null);
        var now = timeProvider.GetUtcNow();
        if (jwt is null
            || !SingleString(jwt, "iss", out var iss) || !SingleString(jwt, "sub", out var sub)
            || !FixedTimeEquals(iss, expectedClientId) || !FixedTimeEquals(sub, expectedClientId)
            || !SingleString(jwt, "aud", out var aud) || aud != (audience ?? stamp.Value.TokenUri).ToString()
            || !jwt.TryGetPayloadValue<long>("exp", out var exp) || !jwt.TryGetPayloadValue<long>("iat", out var iat)
            || exp <= now.ToUnixTimeSeconds()
            || exp - iat > (long)registry.Value.Tokens.MaxAssertionLifetime.TotalSeconds
            || !Jti(jwt, out var jti))
        {
            return Invalid;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp) + registry.Value.Tokens.ProofClockSkew;
        return await RecordAsync(ProofKinds.ClientAssertion, jti!, expiresAt, cancellationToken)
            ?? new ProofValidationResult(true, null);
    }

    public async Task<ProofValidationResult> ValidateDPoPProofAsync(
        string proof, string httpMethod, string requestPath, string? accessToken, bool requireAccessTokenHash,
        CancellationToken cancellationToken)
    {
        JsonWebKey? key;
        try
        {
            var peek = new JsonWebToken(proof);
            using var header = JsonDocument.Parse(Base64UrlEncoder.Decode(peek.EncodedHeader));
            if (!header.RootElement.TryGetProperty("jwk", out var jwkElement) || jwkElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid;
            }

            key = ParseKey(jwkElement.GetRawText());
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or SecurityTokenException or InvalidOperationException)
        {
            return Invalid;
        }

        var jwt = await ParseAndVerifyAsync(proof, key, "dpop+jwt");
        if (key is null || jwt is null)
        {
            return Invalid;
        }

        var path = requestPath;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (!SingleString(jwt, "htm", out var htm) || htm != httpMethod
            || !SingleString(jwt, "htu", out var htu) || htu != stamp.Value.Build(path).ToString()
            || !WithinSkew(jwt) || !Jti(jwt, out var jti))
        {
            return Invalid;
        }

        var hasAth = SingleString(jwt, "ath", out var ath);
        if (requireAccessTokenHash && (!hasAth || accessToken is null))
        {
            return Invalid;
        }

        if (accessToken is not null && hasAth)
        {
            var expected = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
            if (!FixedTimeEquals(ath, expected))
            {
                return Invalid;
            }
        }

        var skew = registry.Value.Tokens.ProofClockSkew;
        var replay = await RecordAsync(ProofKinds.DPoP, jti!, timeProvider.GetUtcNow() + skew + skew, cancellationToken);
        return replay ?? new ProofValidationResult(true, null, null, Base64UrlEncoder.Encode(key.ComputeJwkThumbprint()));
    }

    private static JsonWebKey? ParseKey(string json)
    {
        try
        {
            var key = new JsonWebKey(json);
            return key.Kty == "EC" && key.Crv == "P-256" && string.IsNullOrEmpty(key.D) ? key : null;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonWebToken?> ParseAndVerifyAsync(string token, JsonWebKey? key, string? requiredTyp)
    {
        if (key is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                RequireSignedTokens = true,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                RequireExpirationTime = false,
            });
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt || jwt.Alg != SecurityAlgorithms.EcdsaSha256)
            {
                return null;
            }

            return requiredTyp is null || jwt.Typ == requiredTyp ? jwt : null;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException or JsonException)
        {
            return null;
        }
    }

    private static bool HasHeader(JsonWebToken jwt, string name) => jwt.TryGetHeaderValue<object>(name, out _);

    private static bool SingleString(JsonWebToken jwt, string claim, out string value)
    {
        value = string.Empty;
        if (!jwt.TryGetPayloadValue<string>(claim, out var v) || v is null)
        {
            return false;
        }

        value = v;
        return true;
    }

    private bool WithinSkew(JsonWebToken jwt)
    {
        if (!jwt.TryGetPayloadValue<long>("iat", out var iat))
        {
            return false;
        }

        var delta = Math.Abs(timeProvider.GetUtcNow().ToUnixTimeSeconds() - iat);
        return delta <= (long)registry.Value.Tokens.ProofClockSkew.TotalSeconds;
    }

    private static bool Jti(JsonWebToken jwt, out string? jti) =>
        SingleString(jwt, "jti", out jti) && jti.Length > 0;

    private static bool FixedTimeEquals(string? a, string? b) =>
        a is not null && b is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private async Task<ProofValidationResult?> RecordAsync(
        string kind, string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jti));
        return await replayStore.TryRecordAsync(kind, hash, expiresAt, cancellationToken)
            ? null
            : ProofValidationResult.Refused(ProofRefusalReasons.Replayed);
    }
}
