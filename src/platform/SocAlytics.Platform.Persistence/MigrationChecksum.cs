using System.Security.Cryptography;
using System.Reflection;

namespace SocAlytics.Platform.Persistence;

public static class MigrationChecksum
{
    /// <summary>Lowercase hexadecimal SHA-256 of the exact script bytes.</summary>
    public static string Compute(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static byte[] ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
