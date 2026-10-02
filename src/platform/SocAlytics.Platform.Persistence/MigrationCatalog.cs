namespace SocAlytics.Platform.Persistence;

/// <summary>Supplies the migrations owned by exactly one module.</summary>
public interface IMigrationContributor
{
    PersistenceModuleKey Module { get; }

    IReadOnlyList<MigrationDescriptor> GetMigrations();
}

/// <summary>Validated, deterministically ordered view of every registered migration.</summary>
public sealed class MigrationCatalog
{
    private MigrationCatalog(IReadOnlyList<MigrationDescriptor> migrations) => Migrations = migrations;

    /// <summary>Migrations ordered by adopted module order, then module-local sequence.</summary>
    public IReadOnlyList<MigrationDescriptor> Migrations { get; }

    public static MigrationCatalog Create(IEnumerable<IMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var seenContributors = new HashSet<PersistenceModuleKey>();
        var seenScripts = new HashSet<(PersistenceModuleKey, string)>();
        var seenSequences = new HashSet<(PersistenceModuleKey, int)>();
        var all = new List<MigrationDescriptor>();

        foreach (var contributor in contributors)
        {
            if (!seenContributors.Add(contributor.Module))
            {
                throw new InvalidOperationException(
                    $"Module '{contributor.Module}' registered more than one migration contributor.");
            }

            foreach (var migration in contributor.GetMigrations())
            {
                if (!ReferenceEquals(migration.Module, contributor.Module))
                {
                    throw new InvalidOperationException(
                        $"Module '{contributor.Module}' cannot register migration '{migration.ScriptName}' for module '{migration.Module}'.");
                }

                if (!seenScripts.Add((migration.Module, migration.ScriptName)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration identity '{migration.ScriptName}' for module '{migration.Module}'.");
                }

                if (!seenSequences.Add((migration.Module, migration.Sequence)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration sequence {migration.Sequence} for module '{migration.Module}'.");
                }

                all.Add(migration);
            }
        }

        var ordered = all
            .OrderBy(m => m.Module.Order)
            .ThenBy(m => m.Sequence)
            .ToArray();

        return new MigrationCatalog(ordered);
    }
}
