using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

internal sealed class IdentityAccessMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.IdentityAccess.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.IdentityAccess;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(IdentityAccessMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
