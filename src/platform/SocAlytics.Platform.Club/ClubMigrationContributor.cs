using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

internal sealed class ClubMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.Club.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.Club;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(ClubMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
