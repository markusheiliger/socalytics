using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

/// <summary>An immutable, checksummed module-owned migration script.</summary>
public sealed class MigrationDescriptor
{
    private readonly byte[] _content;

    public MigrationDescriptor(PersistenceModule module, int sequence, string identity, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        Module = module;
        Sequence = sequence;
        Identity = identity;
        _content = content.ToArray();
        Checksum = Convert.ToHexStringLower(SHA256.HashData(_content));
    }

    public PersistenceModule Module { get; }

    public int Sequence { get; }

    public string Identity { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the exact script bytes.</summary>
    public string Checksum { get; }

    public ReadOnlyMemory<byte> Content => _content;

    public string Script => System.Text.Encoding.UTF8.GetString(_content);

    public static MigrationDescriptor FromEmbeddedResource(
        PersistenceModule module, int sequence, string identity, Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return new MigrationDescriptor(module, sequence, identity, buffer.ToArray());
    }
}
