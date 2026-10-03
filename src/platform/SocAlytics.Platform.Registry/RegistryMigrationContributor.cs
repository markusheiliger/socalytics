using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Registry;

internal sealed class RegistryMigrationContributor : IMigrationContributor
{
    public PersistenceModule Module => PersistenceModule.Registry;

    public IEnumerable<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(RegistryMigrationContributor).Assembly,
            "SocAlytics.Platform.Registry.Migrations.0001_initial.sql"),
    ];
}
