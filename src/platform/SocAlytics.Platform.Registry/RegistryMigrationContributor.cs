using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Registry;

internal sealed class RegistryMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.Registry.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.Registry;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(RegistryMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
