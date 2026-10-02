namespace SocAlytics.Platform.Persistence;

public sealed class MigrationCatalog
{
    private readonly IReadOnlyList<MigrationDescriptor> _migrations;

    public MigrationCatalog(IEnumerable<MigrationDescriptor> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var entries = migrations.ToArray();
        if (entries.Any(migration => migration is null))
        {
            throw new ArgumentException("Migration descriptors cannot contain null entries.", nameof(migrations));
        }

        var identities = new HashSet<(PersistenceModuleIdentity Module, string Identity)>();
        var sequences = new HashSet<(PersistenceModuleIdentity Module, int Sequence)>();
        foreach (var migration in entries)
        {
            if (!identities.Add((migration.Module, migration.Identity)))
            {
                throw new ArgumentException(
                    $"Module '{migration.Module.Key}' has duplicate migration identity '{migration.Identity}'.",
                    nameof(migrations));
            }

            if (!sequences.Add((migration.Module, migration.Sequence)))
            {
                throw new ArgumentException(
                    $"Module '{migration.Module.Key}' has duplicate migration sequence {migration.Sequence}.",
                    nameof(migrations));
            }
        }

        _migrations = Array.AsReadOnly(entries);
    }

    public IReadOnlyList<MigrationDescriptor> Migrations => _migrations;
}
