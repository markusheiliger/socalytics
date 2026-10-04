namespace SocAlytics.Platform.Persistence;

/// <summary>Validated migrations of all registered contributors in module and sequence order.</summary>
public sealed class MigrationCatalog
{
    public MigrationCatalog(IEnumerable<IMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var all = new List<MigrationDescriptor>();
        var modules = new HashSet<PersistenceModuleKey>();

        foreach (var contributor in contributors)
        {
            if (!modules.Add(contributor.Module))
            {
                throw new InvalidOperationException(
                    $"Module '{contributor.Module}' registered more than one migration contributor.");
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            var sequences = new HashSet<int>();

            foreach (var migration in contributor.GetMigrations())
            {
                if (!ReferenceEquals(migration.Module, contributor.Module))
                {
                    throw new InvalidOperationException(
                        $"Migration '{migration.Identity}' belongs to module '{migration.Module}' but was contributed by '{contributor.Module}'.");
                }

                if (!identities.Add(migration.Identity))
                {
                    throw new InvalidOperationException(
                        $"Module '{migration.Module}' declares migration identity '{migration.Identity}' more than once.");
                }

                if (!sequences.Add(migration.Sequence))
                {
                    throw new InvalidOperationException(
                        $"Module '{migration.Module}' declares migration sequence {migration.Sequence} more than once.");
                }

                all.Add(migration);
            }
        }

        Migrations = all.OrderBy(m => m.Module.Order).ThenBy(m => m.Sequence).ToArray();
    }

    public IReadOnlyList<MigrationDescriptor> Migrations { get; }
}
