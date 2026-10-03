using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Recordings;

internal sealed class RecordingsMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.Recordings.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.Recordings;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.Recordings, 1, "0001_initial_schema", typeof(RecordingsMigrationContributor).Assembly, InitialResource)
    ];
}
