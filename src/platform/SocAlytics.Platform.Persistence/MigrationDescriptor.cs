using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

/// <summary>An embedded, immutable, checksummed module migration script.</summary>
public sealed class MigrationDescriptor
{
    private readonly byte[] content;

    public MigrationDescriptor(PersistenceModuleKey module, int sequence, string identity, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        Module = module;
        Sequence = sequence;
        Identity = identity;
        this.content = (byte[])content.Clone();
        Checksum = ComputeChecksum(this.content);
    }

    public PersistenceModuleKey Module { get; }

    /// <summary>Strictly positive, module-local ordering.</summary>
    public int Sequence { get; }

    public string Identity { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the script bytes.</summary>
    public string Checksum { get; }

    public string Script => System.Text.Encoding.UTF8.GetString(content);

    public static string ComputeChecksum(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static MigrationDescriptor FromEmbeddedResource(
        Assembly assembly,
        string resourceName,
        PersistenceModuleKey module,
        int sequence,
        string identity)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in '{assembly.GetName().Name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return new MigrationDescriptor(module, sequence, identity, buffer.ToArray());
    }
}
