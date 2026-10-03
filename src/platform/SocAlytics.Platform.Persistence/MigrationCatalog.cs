namespace SocAlytics.Platform.Persistence;

/// <summary>The validated, deterministically ordered set of all registered migrations.</summary>
internal sealed class MigrationCatalog
{
    private MigrationCatalog(IReadOnlyList<MigrationDescriptor> migrations)
    {
        Migrations = migrations;
    }

    public IReadOnlyList<MigrationDescriptor> Migrations { get; }

    public static MigrationCatalog Create(IEnumerable<IModuleMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var all = new List<MigrationDescriptor>();
        var contributorModules = new HashSet<ModuleKey>();
        var scripts = new HashSet<(ModuleKey, string)>();
        var sequences = new HashSet<(ModuleKey, int)>();

        foreach (var contributor in contributors)
        {
            if (!contributorModules.Add(contributor.Module))
            {
                throw new InvalidOperationException($"Module '{contributor.Module}' registered more than one migration contributor.");
            }

            foreach (var migration in contributor.Migrations)
            {
                if (migration.Module != contributor.Module)
                {
                    throw new InvalidOperationException(
                        $"Module '{contributor.Module}' attempted to register migration '{migration.ScriptName}' for module '{migration.Module}'.");
                }

                if (!scripts.Add((migration.Module, migration.ScriptName)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration script identity '{migration.ScriptName}' for module '{migration.Module}'.");
                }

                if (!sequences.Add((migration.Module, migration.Sequence)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration sequence {migration.Sequence} for module '{migration.Module}'.");
                }

                all.Add(migration);
            }
        }

        return new MigrationCatalog(
            all.OrderBy(m => m.Module.Order).ThenBy(m => m.Sequence).ToArray());
    }
}
