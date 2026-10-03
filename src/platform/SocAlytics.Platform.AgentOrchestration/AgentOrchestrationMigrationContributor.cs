using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.AgentOrchestration;

internal sealed class AgentOrchestrationMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.AgentOrchestration.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.AgentOrchestration;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.AgentOrchestration, 1, "0001_initial_schema", typeof(AgentOrchestrationMigrationContributor).Assembly, InitialResource)
    ];
}
