using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Recordings;

internal sealed class RecordingsMigrationContributor : IMigrationContributor
{
    public PersistenceModule Module => PersistenceModule.Recordings;

    public IEnumerable<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(RecordingsMigrationContributor).Assembly,
            "SocAlytics.Platform.Recordings.Migrations.0001_initial.sql"),
    ];
}
