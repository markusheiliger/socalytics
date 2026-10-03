using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Analysis;

internal sealed class AnalysisMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.Analysis.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.Analysis;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.Analysis, 1, "0001_initial_schema", typeof(AnalysisMigrationContributor).Assembly, InitialResource)
    ];
}
