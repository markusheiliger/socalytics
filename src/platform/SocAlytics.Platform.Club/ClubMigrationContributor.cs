using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

internal sealed class ClubMigrationContributor : IMigrationContributor
{
    public PersistenceModuleKey Module => PersistenceModuleKey.Club;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(ClubMigrationContributor).Assembly,
            "SocAlytics.Platform.Club.Migrations.0001_initial.sql"),
    ];
}
