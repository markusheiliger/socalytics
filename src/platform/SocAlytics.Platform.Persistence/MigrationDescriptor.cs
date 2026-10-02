using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

/// <summary>An embedded, forward-only module migration with a stable identity and SHA-256 checksum.</summary>
public sealed class MigrationDescriptor
{
    private readonly byte[] _content;

    public MigrationDescriptor(PersistenceModuleKey module, int sequence, string scriptName, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        if (content.IsEmpty)
        {
            throw new ArgumentException("A migration must contain script content.", nameof(content));
        }

        Module = module;
        Sequence = sequence;
        ScriptName = scriptName;
        _content = content.ToArray();
        Checksum = ComputeChecksum(_content);
    }

    public PersistenceModuleKey Module { get; }

    public int Sequence { get; }

    public string ScriptName { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the exact script bytes.</summary>
    public string Checksum { get; }

    public ReadOnlyMemory<byte> Content => _content;

    public static string ComputeChecksum(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static MigrationDescriptor FromEmbeddedResource(
        PersistenceModuleKey module,
        int sequence,
        string scriptName,
        Assembly assembly,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return new MigrationDescriptor(module, sequence, scriptName, buffer.ToArray());
    }
}
