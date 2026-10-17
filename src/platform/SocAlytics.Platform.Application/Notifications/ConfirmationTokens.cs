using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Platform.Application.Notifications;

/// <summary>The one definition of confirmation tokens: 256 random bits as base64url, stored only as the SHA-256 of that text.</summary>
public static class ConfirmationTokens
{
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
