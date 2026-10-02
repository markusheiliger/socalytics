using System.Collections.Immutable;

namespace SocAlytics.Platform.Persistence;

/// <summary>A module's contribution of migrations. A contributor serves exactly one module.</summary>
public interface IMigrationContributor
{
    PersistenceModuleKey Module { get; }

    IEnumerable<MigrationDescriptor> GetMigrations();
}

/// <summary>Validated migrations in deterministic module and sequence order.</summary>
public sealed class MigrationCatalog
{
    private MigrationCatalog(ImmutableArray<MigrationDescriptor> migrations) => Migrations = migrations;

    public ImmutableArray<MigrationDescriptor> Migrations { get; }

    public static MigrationCatalog Create(IEnumerable<IMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var all = new List<MigrationDescriptor>();
        var contributedModules = new HashSet<PersistenceModuleKey>();

        foreach (var contributor in contributors)
        {
            if (!contributedModules.Add(contributor.Module))
            {
                throw new InvalidOperationException(
                    $"Module '{contributor.Module}' registered more than one migration contributor.");
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

        foreach (var group in all.GroupBy(m => m.Module))
        {
            var duplicateIdentity = group.GroupBy(m => m.Identity, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicateIdentity is not null)
            {
                throw new InvalidOperationException(
                    $"Module '{group.Key}' registered duplicate migration identity '{duplicateIdentity.Key}'.");
            }

            var duplicateSequence = group.GroupBy(m => m.Sequence).FirstOrDefault(g => g.Count() > 1);
            if (duplicateSequence is not null)
            {
                throw new InvalidOperationException(
                    $"Module '{group.Key}' registered duplicate migration sequence {duplicateSequence.Key}.");
            }
        }

        return new MigrationCatalog([.. all.OrderBy(m => m.Module.Order).ThenBy(m => m.Sequence)]);
    }
}
