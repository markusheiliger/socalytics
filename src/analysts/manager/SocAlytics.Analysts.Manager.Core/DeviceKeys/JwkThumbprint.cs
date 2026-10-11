using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public static class JwkThumbprint
{
    public static byte[] ComputeHash(PublicJwk jwk)
    {
        // RFC 7638: required members in lexicographic order, no whitespace.
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    public static string Compute(PublicJwk jwk) => PublicJwk.Base64Url(ComputeHash(jwk));
}
