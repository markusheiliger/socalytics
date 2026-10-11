using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Platform;

public sealed class ProofFactory(TimeProvider time)
{
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromSeconds(60);

    public string ActivationProof(IDeviceKey key, string activationEndpoint, string challenge) =>
        CompactJws.Sign(
            key,
            new JsonObject { ["typ"] = "analyst-manager-activation+jwt" },
            new JsonObject
            {
                ["aud"] = activationEndpoint,
                ["challenge"] = challenge,
                ["iat"] = Now(),
                ["jti"] = NewJti(),
            });

    public string ClientAssertion(IDeviceKey key, string managerId, string tokenEndpoint)
    {
        var iat = Now();
        return CompactJws.Sign(
            key,
            new JsonObject { ["typ"] = "JWT" },
            new JsonObject
            {
                ["iss"] = managerId,
                ["sub"] = managerId,
                ["aud"] = tokenEndpoint,
                ["iat"] = iat,
                ["exp"] = iat + (long)AssertionLifetime.TotalSeconds,
                ["jti"] = NewJti(),
            });
    }

    public string DPoP(IDeviceKey key, string method, string uri, string? accessToken = null)
    {
        var payload = new JsonObject
        {
            ["htm"] = method,
            ["htu"] = uri,
            ["iat"] = Now(),
            ["jti"] = NewJti(),
        };
        if (accessToken is not null)
        {
            payload["ath"] = PublicJwk.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
        }

        return CompactJws.Sign(
            key,
            new JsonObject { ["typ"] = "dpop+jwt", ["jwk"] = key.PublicJwk.ToJsonObject() },
            payload);
    }

    private long Now() => time.GetUtcNow().ToUnixTimeSeconds();

    private static string NewJti() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
