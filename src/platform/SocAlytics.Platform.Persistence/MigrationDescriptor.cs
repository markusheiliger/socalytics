using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

/// <summary>An immutable, module-owned migration script with its SHA-256 content checksum.</summary>
public sealed class MigrationDescriptor
{
    private readonly byte[] _content;

    public MigrationDescriptor(PersistenceModuleKey module, int sequence, string identity, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(content);

        Module = module;
        Sequence = sequence;
        Identity = identity;
        _content = (byte[])content.Clone();
        Checksum = ComputeChecksum(_content);
    }

    public PersistenceModuleKey Module { get; }

    public int Sequence { get; }

    public string Identity { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the script bytes.</summary>
    public string Checksum { get; }

    public ReadOnlyMemory<byte> Content => _content;

    public string Script => System.Text.Encoding.UTF8.GetString(_content);

    public static string ComputeChecksum(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static MigrationDescriptor FromEmbeddedResource(
        PersistenceModuleKey module, int sequence, string identity, Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in '{assembly.GetName().Name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return new MigrationDescriptor(module, sequence, identity, buffer.ToArray());
    }
}
