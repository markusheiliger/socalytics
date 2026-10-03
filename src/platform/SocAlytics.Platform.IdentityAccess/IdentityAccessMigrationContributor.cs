using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

internal sealed class IdentityAccessMigrationContributor : IMigrationContributor
{
    public PersistenceModule Module => PersistenceModule.IdentityAccess;

    public IEnumerable<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(IdentityAccessMigrationContributor).Assembly,
            "SocAlytics.Platform.IdentityAccess.Migrations.0001_initial.sql"),
    ];
}
