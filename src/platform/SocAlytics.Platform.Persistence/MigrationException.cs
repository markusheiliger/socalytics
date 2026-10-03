namespace SocAlytics.Platform.Persistence;

public enum MigrationFailureKind
{
    ChecksumConflict,
    ScriptFailed,
    Infrastructure,
}

/// <summary>
/// A migration failure. Messages identify only the module and script and never contain SQL text,
/// connection strings, or server error details.
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException(MigrationFailureKind kind, string message, string? module = null, string? identity = null)
        : base(message)
    {
        Kind = kind;
        Module = module;
        Identity = identity;
    }

    public MigrationFailureKind Kind { get; }

    public string? Module { get; }

    public string? Identity { get; }
}
