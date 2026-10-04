using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.AgentOrchestration;

internal sealed class AgentOrchestrationMigrationContributor : IMigrationContributor
{
    public PersistenceModuleKey Module => PersistenceModuleKey.AgentOrchestration;

    public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
    [
        MigrationDescriptor.FromEmbeddedResource(
            Module, 1, "0001_initial", typeof(AgentOrchestrationMigrationContributor).Assembly,
            "SocAlytics.Platform.AgentOrchestration.Migrations.0001_initial.sql"),
    ];
}
