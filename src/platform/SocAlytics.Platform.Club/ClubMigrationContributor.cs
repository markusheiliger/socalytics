using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Club;

internal sealed class ClubMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.Club.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.Club;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.Club, 1, "0001_initial_schema", typeof(ClubMigrationContributor).Assembly, InitialResource)
    ];
}
