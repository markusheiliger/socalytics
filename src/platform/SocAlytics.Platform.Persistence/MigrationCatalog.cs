namespace SocAlytics.Platform.Persistence;

/// <summary>The validated, deterministically ordered set of all registered migrations.</summary>
public sealed class MigrationCatalog
{
    private MigrationCatalog(IReadOnlyList<MigrationDescriptor> migrations) => Migrations = migrations;

    /// <summary>Ordered by adopted module order, then module-local sequence.</summary>
    public IReadOnlyList<MigrationDescriptor> Migrations { get; }

    public static MigrationCatalog Create(IEnumerable<IMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var all = new List<MigrationDescriptor>();
        var contributingModules = new HashSet<PersistenceModule>();

        foreach (var contributor in contributors)
        {
            if (!contributingModules.Add(contributor.Module))
            {
                throw new InvalidOperationException($"Module '{contributor.Module}' registered more than one migration contributor.");
            }

            foreach (var migration in contributor.GetMigrations())
            {
                if (migration.Module != contributor.Module)
                {
                    throw new InvalidOperationException(
                        $"Module '{contributor.Module}' cannot register migration '{migration.Identity}' for module '{migration.Module}'.");
                }

                all.Add(migration);
            }
        }

        var sequences = new HashSet<(PersistenceModule, int)>();
        var identities = new HashSet<(PersistenceModule, string)>();

        foreach (var migration in all)
        {
            if (!sequences.Add((migration.Module, migration.Sequence)))
            {
                throw new InvalidOperationException(
                    $"Module '{migration.Module}' registered sequence {migration.Sequence} more than once.");
            }

            if (!identities.Add((migration.Module, migration.Identity)))
            {
                throw new InvalidOperationException(
                    $"Module '{migration.Module}' registered migration identity '{migration.Identity}' more than once.");
            }
        }

        return new MigrationCatalog(
            all.OrderBy(m => m.Module.Order).ThenBy(m => m.Sequence).ToArray());
    }
}
