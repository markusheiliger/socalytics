using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Computes the stable SHA-256 checksum used to detect edited embedded migration content.
/// </summary>
public static class MigrationChecksum
{
    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public static string Compute(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
