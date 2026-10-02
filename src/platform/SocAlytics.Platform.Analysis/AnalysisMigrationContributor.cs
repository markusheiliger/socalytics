using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Analysis;

internal sealed class AnalysisMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.Analysis.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.Analysis;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(AnalysisMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
