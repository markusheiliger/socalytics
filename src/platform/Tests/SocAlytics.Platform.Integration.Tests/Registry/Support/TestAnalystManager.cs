using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

// In-memory ECDSA P-256 key that builds proofs; every claim and header can be overridden for negative cases.
internal sealed class TestAnalystManager : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestAnalystManager(string clientId = "manager-1") => ClientId = clientId;

    public string ClientId { get; }

    public string PublicJwk => JsonSerializer.Serialize(PublicJwkObject());

    public Dictionary<string, object> PublicJwkObject()
    {
        var p = _key.ExportParameters(false);
        return new()
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64UrlEncoder.Encode(p.Q.X!),
            ["y"] = Base64UrlEncoder.Encode(p.Q.Y!),
        };
    }

    public string Thumbprint => Base64UrlEncoder.Encode(new JsonWebKey(PublicJwk).ComputeJwkThumbprint());

    public static string AccessTokenHash(string token) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    public string ActivationProof(
        DateTimeOffset now, string aud, string challenge = "challenge-1", string? jti = null,
        Action<Dictionary<string, object>>? claims = null, Action<Dictionary<string, object>>? header = null,
        TestAnalystManager? signer = null)
    {
        var c = new Dictionary<string, object>
        {
            ["aud"] = aud, ["challenge"] = challenge, ["iat"] = now.ToUnixTimeSeconds(), ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        };
        var h = new Dictionary<string, object> { ["typ"] = "analyst-manager-activation+jwt" };
        claims?.Invoke(c);
        header?.Invoke(h);
        return Sign(c, h, signer);
    }

    public string ClientAssertion(
        DateTimeOffset now, string aud, string? clientId = null, TimeSpan? lifetime = null, string? jti = null,
        Action<Dictionary<string, object>>? claims = null, Action<Dictionary<string, object>>? header = null,
        TestAnalystManager? signer = null)
    {
        var id = clientId ?? ClientId;
        var c = new Dictionary<string, object>
        {
            ["iss"] = id, ["sub"] = id, ["aud"] = aud, ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + (lifetime ?? TimeSpan.FromSeconds(60))).ToUnixTimeSeconds(), ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        };
        var h = new Dictionary<string, object>();
        claims?.Invoke(c);
        header?.Invoke(h);
        return Sign(c, h, signer);
    }

    public string DPoPProof(
        DateTimeOffset now, string htm, string htu, string? accessToken = null, string? jti = null,
        Action<Dictionary<string, object>>? claims = null, Action<Dictionary<string, object>>? header = null,
        TestAnalystManager? signer = null)
    {
        var c = new Dictionary<string, object>
        {
            ["htm"] = htm, ["htu"] = htu, ["iat"] = now.ToUnixTimeSeconds(), ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        };
        if (accessToken is not null)
        {
            c["ath"] = AccessTokenHash(accessToken);
        }

        var h = new Dictionary<string, object> { ["typ"] = "dpop+jwt", ["jwk"] = PublicJwkObject() };
        claims?.Invoke(c);
        header?.Invoke(h);
        return Sign(c, h, signer);
    }

    private string Sign(Dictionary<string, object> claims, Dictionary<string, object> header, TestAnalystManager? signer)
    {
        var alg = header.TryGetValue("alg", out var a) ? (string)a : "ES256";
        var h = new Dictionary<string, object>(header) { ["alg"] = alg };
        var encodedHeader = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(h));
        var encodedPayload = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var input = $"{encodedHeader}.{encodedPayload}";
        if (alg == "none")
        {
            return input + ".";
        }

        var key = signer?._key ?? _key;
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256);
        return $"{input}.{Base64UrlEncoder.Encode(signature)}";
    }

    public void Dispose() => _key.Dispose();
}
