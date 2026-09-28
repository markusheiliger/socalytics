namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Builds the deterministic, module-then-sequence-ordered set of migrations contributed by
/// every registered module, rejecting any contributor that registers migrations for another
/// module's key or that duplicates an identity or sequence within its own module.
/// </summary>
public sealed class MigrationCatalog
{
    private static readonly ModuleKey[] AdoptedModuleOrder =
    [
        ModuleKey.Club,
        ModuleKey.IdentityAccess,
        ModuleKey.Recordings,
        ModuleKey.Registry,
        ModuleKey.Analysis,
        ModuleKey.AgentOrchestration
    ];

    private MigrationCatalog(IReadOnlyList<MigrationDescriptor> migrations)
    {
        Migrations = migrations;
    }

    public IReadOnlyList<MigrationDescriptor> Migrations { get; }

    public static MigrationCatalog Create(IEnumerable<IModuleMigrationContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        var descriptors = new List<MigrationDescriptor>();
        var seenIdentities = new HashSet<(ModuleKey ModuleKey, string ScriptIdentity)>();
        var seenSequences = new HashSet<(ModuleKey ModuleKey, int Sequence)>();

        foreach (var contributor in contributors)
        {
            foreach (var descriptor in contributor.GetMigrations())
            {
                if (descriptor.ModuleKey != contributor.ModuleKey)
                {
                    throw new InvalidOperationException(
                        $"Contributor for module '{contributor.ModuleKey}' must not register a migration for module '{descriptor.ModuleKey}'.");
                }

                if (!seenIdentities.Add((descriptor.ModuleKey, descriptor.ScriptIdentity)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration identity '{descriptor.ScriptIdentity}' registered for module '{descriptor.ModuleKey}'.");
                }

                if (!seenSequences.Add((descriptor.ModuleKey, descriptor.Sequence)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate migration sequence '{descriptor.Sequence}' registered for module '{descriptor.ModuleKey}'.");
                }

                descriptors.Add(descriptor);
            }
        }

        var ordered = descriptors
            .OrderBy(descriptor => Array.IndexOf(AdoptedModuleOrder, descriptor.ModuleKey))
            .ThenBy(descriptor => descriptor.Sequence)
            .ToArray();

        return new MigrationCatalog(ordered);
    }
}
