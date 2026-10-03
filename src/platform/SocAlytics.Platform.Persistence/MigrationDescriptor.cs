using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Persistence;

public sealed partial class MigrationDescriptor
{
    private readonly byte[] _content;

    public MigrationDescriptor(ModuleKey module, int sequence, string scriptName, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        if (!ScriptNamePattern().IsMatch(scriptName))
        {
            throw new ArgumentException("Script identity may only contain letters, digits, '_', '-', and '.'.", nameof(scriptName));
        }

        if (content.IsEmpty)
        {
            throw new ArgumentException("Migration content must not be empty.", nameof(content));
        }

        Module = module;
        Sequence = sequence;
        ScriptName = scriptName;
        _content = content.ToArray();
        Checksum = MigrationChecksum.Compute(_content);
    }

    public ModuleKey Module { get; }

    /// <summary>Module-local, monotonically increasing position.</summary>
    public int Sequence { get; }

    /// <summary>Stable script identity, unique within its module.</summary>
    public string ScriptName { get; }

    public string Checksum { get; }

    public ReadOnlyMemory<byte> Content => _content;

    internal string Sql => Encoding.UTF8.GetString(_content);

    public static MigrationDescriptor FromEmbeddedResource(
        ModuleKey module, int sequence, string scriptName, Assembly assembly, string resourceName) =>
        new(module, sequence, scriptName, MigrationChecksum.ReadEmbeddedResource(assembly, resourceName));

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex ScriptNamePattern();
}
