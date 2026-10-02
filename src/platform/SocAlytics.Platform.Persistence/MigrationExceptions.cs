namespace SocAlytics.Platform.Persistence;

/// <summary>Sanitized migration failure; messages never contain SQL bodies or connection strings.</summary>
public class MigrationFailedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string? Module { get; init; }

    public string? ScriptName { get; init; }
}

/// <summary>An applied script's recorded checksum differs from the embedded script.</summary>
public sealed class MigrationChecksumConflictException(string module, string scriptName)
    : MigrationFailedException($"Applied migration '{scriptName}' of module '{module}' has a checksum that no longer matches its embedded script.")
{
    public new string Module { get; } = module;

    public new string ScriptName { get; } = scriptName;
}
