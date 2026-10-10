using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocAlytics.Platform.Domain.Registry;

public sealed class DeviceKeyThumbprint
{
    public const string RejectionMessage =
        "EC P-256 public JWK (kty, crv, x, y only); no private members accepted";

    private DeviceKeyThumbprint(string value, byte[] hash)
    {
        Value = value;
        Hash = hash;
    }

    public string Value { get; }

    internal byte[] Hash { get; }

    public override string ToString() => Value;

    public static bool TryCreate(JsonElement jwk, out DeviceKeyThumbprint? thumbprint)
    {
        thumbprint = null;
        if (jwk.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? kty = null, crv = null, x = null, y = null;
        var count = 0;
        foreach (var property in jwk.EnumerateObject())
        {
            count++;
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = property.Value.GetString();
            switch (property.Name)
            {
                case "kty": kty = value; break;
                case "crv": crv = value; break;
                case "x": x = value; break;
                case "y": y = value; break;
                default: return false;
            }
        }

        if (count != 4 || kty != "EC" || crv != "P-256" || !IsCoordinate(x) || !IsCoordinate(y))
        {
            return false;
        }

        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        thumbprint = new DeviceKeyThumbprint(Base64Url(hash), hash);
        return true;
    }

    private static bool IsCoordinate(string? value)
    {
        if (value is null || value.Length != 43)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
