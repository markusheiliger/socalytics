using System.Reflection;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Describes one module-owned, embedded migration script: its owning module, its stable
/// one-based module-local sequence, its stable script identity, its content, and the
/// content's SHA-256 checksum used for repeat-run and conflict detection.
/// </summary>
public sealed class MigrationDescriptor
{
    public MigrationDescriptor(ModuleKey moduleKey, int sequence, string scriptIdentity, byte[] content)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "A migration sequence must be a positive, one-based, module-local ordinal.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scriptIdentity);
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            throw new ArgumentException("Migration content must not be empty.", nameof(content));
        }

        ModuleKey = moduleKey;
        Sequence = sequence;
        ScriptIdentity = scriptIdentity;
        Content = content;
        Checksum = MigrationChecksum.Compute(content);
    }

    public ModuleKey ModuleKey { get; }

    public int Sequence { get; }

    public string ScriptIdentity { get; }

    public byte[] Content { get; }

    public string Checksum { get; }

    /// <summary>
    /// Builds a descriptor from an embedded resource, so its checksum is calculated from the
    /// exact bytes the owning module ships.
    /// </summary>
    public static MigrationDescriptor FromEmbeddedResource(
        Assembly assembly,
        string resourceName,
        ModuleKey moduleKey,
        int sequence,
        string scriptIdentity)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");

        using var buffer = new MemoryStream();
        resourceStream.CopyTo(buffer);

        return new MigrationDescriptor(moduleKey, sequence, scriptIdentity, buffer.ToArray());
    }
}
