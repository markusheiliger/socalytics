using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

internal sealed class IdentityAccessMigrationContributor : IMigrationContributor
{
    public PersistenceModuleKey Module => PersistenceModuleKey.IdentityAccess;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(IdentityAccessMigrationContributor).Assembly,
            "SocAlytics.Platform.IdentityAccess.Migrations.0001_initial.sql"),
    ];
}
