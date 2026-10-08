namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal enum MigrationStateKind
{
    Current,
    Pending,
    ChecksumMismatch,
    SequenceConflict,
}

internal sealed class MigrationState
{
    private MigrationState(
        MigrationStateKind kind,
        IReadOnlyList<MigrationScript> pendingScripts,
        string? identity,
        IReadOnlyList<string> unknownAppliedIdentities)
    {
        Kind = kind;
        PendingScripts = pendingScripts;
        Identity = identity;
        UnknownAppliedIdentities = unknownAppliedIdentities;
    }

    public MigrationStateKind Kind { get; }

    /// <summary>Pending scripts in ascending sequence order; empty unless <see cref="Kind"/> is Pending.</summary>
    public IReadOnlyList<MigrationScript> PendingScripts { get; }

    /// <summary>The offending identity for a checksum mismatch or sequence conflict.</summary>
    public string? Identity { get; }

    public IReadOnlyList<string> UnknownAppliedIdentities { get; }

    public static MigrationState Current(IReadOnlyList<string> unknown) =>
        new(MigrationStateKind.Current, [], null, unknown);

    public static MigrationState Pending(IReadOnlyList<MigrationScript> pending, IReadOnlyList<string> unknown) =>
        new(MigrationStateKind.Pending, pending, null, unknown);

    public static MigrationState ChecksumMismatch(string identity, IReadOnlyList<string> unknown) =>
        new(MigrationStateKind.ChecksumMismatch, [], identity, unknown);

    public static MigrationState SequenceConflict(string identity, IReadOnlyList<string> unknown) =>
        new(MigrationStateKind.SequenceConflict, [], identity, unknown);
}
