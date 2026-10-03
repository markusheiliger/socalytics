using System.Reflection;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.IdentityAccess;

internal sealed class IdentityAccessMigrationContributor : IModuleMigrationContributor
{
    private const string InitialResource = "SocAlytics.Platform.IdentityAccess.Migrations.0001_initial_schema.sql";

    public ModuleKey Module => ModuleKey.IdentityAccess;

    public IReadOnlyList<MigrationDescriptor> Migrations { get; } =
    [
        MigrationDescriptor.FromEmbeddedResource(
            ModuleKey.IdentityAccess, 1, "0001_initial_schema", typeof(IdentityAccessMigrationContributor).Assembly, InitialResource)
    ];
}
