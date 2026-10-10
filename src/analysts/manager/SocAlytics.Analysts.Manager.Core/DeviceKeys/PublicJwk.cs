using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public sealed record PublicJwk(string X, string Y)
{
    public static PublicJwk FromParameters(ECParameters parameters)
    {
        if (parameters.Q.X is null || parameters.Q.Y is null)
        {
            throw new ArgumentException("Public point missing.", nameof(parameters));
        }

        return new PublicJwk(Base64Url(parameters.Q.X), Base64Url(parameters.Q.Y));
    }

    // Private members such as "d" are ignored.
    public static PublicJwk FromJson(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Invalid JWK.");
        if ((string?)node["kty"] != "EC" || (string?)node["crv"] != "P-256")
        {
            throw new FormatException("Only EC P-256 keys are supported.");
        }

        return new PublicJwk(
            (string?)node["x"] ?? throw new FormatException("Invalid JWK."),
            (string?)node["y"] ?? throw new FormatException("Invalid JWK."));
    }

    public ECParameters ToParameters() => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = FromBase64Url(X), Y = FromBase64Url(Y) },
    };

    public JsonObject ToJsonObject() => new()
    {
        ["kty"] = "EC",
        ["crv"] = "P-256",
        ["x"] = X,
        ["y"] = Y,
    };

    public string ToJson() => ToJsonObject().ToJsonString();

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return Convert.FromBase64String(s);
    }
}
