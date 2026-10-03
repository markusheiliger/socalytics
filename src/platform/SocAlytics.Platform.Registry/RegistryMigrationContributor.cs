using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Registry;

internal sealed class RegistryMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.Registry.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.Registry;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.Registry, 1, "0001_initial_schema", typeof(RegistryMigrationContributor).Assembly, InitialResource)
    ];
}
