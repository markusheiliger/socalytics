using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

public sealed class MigrationDescriptor
{
    private MigrationDescriptor(
        ModuleIdentity module,
        int sequence,
        string identity,
        byte[] content,
        byte[] checksum)
    {
        Module = module;
        Sequence = sequence;
        Identity = identity;
        Content = content;
        Checksum = checksum;
    }

    public ModuleIdentity Module { get; }

    public int Sequence { get; }

    public string Identity { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public ReadOnlyMemory<byte> Checksum { get; }

    public static MigrationDescriptor FromEmbeddedResource(
        ModuleIdentity module,
        int sequence,
        string identity,
        Assembly assembly,
        string resourceName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        _ = ModuleIdentityCatalog.GetKey(module);

        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException(
                $"Embedded migration resource '{resourceName}' was not found.",
                nameof(resourceName));
        using var content = new MemoryStream();
        resource.CopyTo(content);

        var bytes = content.ToArray();
        return new MigrationDescriptor(module, sequence, identity, bytes, SHA256.HashData(bytes));
    }
}
