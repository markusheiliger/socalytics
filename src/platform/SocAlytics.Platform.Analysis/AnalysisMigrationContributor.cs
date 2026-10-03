using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Analysis;

internal sealed class AnalysisMigrationContributor : IMigrationContributor
{
    public PersistenceModule Module => PersistenceModule.Analysis;

    public IEnumerable<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(AnalysisMigrationContributor).Assembly,
            "SocAlytics.Platform.Analysis.Migrations.0001_initial.sql"),
    ];
}
