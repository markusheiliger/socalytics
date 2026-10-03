using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

internal sealed class ClubMigrationContributor : IMigrationContributor
{
    public PersistenceModule Module => PersistenceModule.Club;

    public IEnumerable<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(ClubMigrationContributor).Assembly,
            "SocAlytics.Platform.Club.Migrations.0001_initial.sql"),
    ];
}
