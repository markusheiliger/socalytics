using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Recordings;

internal sealed class RecordingsMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.Recordings.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.Recordings;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(RecordingsMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
