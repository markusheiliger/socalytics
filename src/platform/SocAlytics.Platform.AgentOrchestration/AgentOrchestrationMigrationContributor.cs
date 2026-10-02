using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.AgentOrchestration;

internal sealed class AgentOrchestrationMigrationContributor : IMigrationContributor
{
    private const string InitialSchemaResource = "SocAlytics.Platform.AgentOrchestration.Migrations.0001_initial_schema.sql";

    public PersistenceModuleKey Module => PersistenceModuleKey.AgentOrchestration;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial_schema", typeof(AgentOrchestrationMigrationContributor).Assembly, InitialSchemaResource),
    ];
}
