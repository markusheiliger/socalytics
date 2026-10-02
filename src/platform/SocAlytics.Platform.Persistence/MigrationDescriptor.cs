using System.Reflection;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Persistence;

public sealed class MigrationDescriptor
{
    private readonly byte[] _script;

    private MigrationDescriptor(
        PersistenceModuleIdentity module,
        int sequence,
        string identity,
        byte[] script)
    {
        Module = module;
        Sequence = sequence;
        Identity = identity;
        _script = script;
        Checksum = Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant();
    }

    public PersistenceModuleIdentity Module { get; }

    public int Sequence { get; }

    public string Identity { get; }

    public string Checksum { get; }

    public static MigrationDescriptor FromEmbeddedResource(
        PersistenceModuleIdentity module,
        int sequence,
        string identity,
        Assembly assembly,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        if (!PersistenceModuleIdentity.All.Contains(module))
        {
            throw new ArgumentException("The module identity is not adopted.", nameof(module));
        }

        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException(
                $"Embedded migration resource '{resourceName}' was not found.",
                nameof(resourceName));
        using var content = new MemoryStream();
        resource.CopyTo(content);

        return new MigrationDescriptor(module, sequence, identity, content.ToArray());
    }

    internal Stream OpenScript()
    {
        return new MemoryStream(_script, writable: false);
    }
}
