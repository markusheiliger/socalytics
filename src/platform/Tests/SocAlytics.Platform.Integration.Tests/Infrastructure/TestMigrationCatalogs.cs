using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

internal static class TestMigrationCatalogs
{
    private const string ResourcePrefix = "SocAlytics.Platform.Integration.Tests.TestMigrations.";

    public static MigrationCatalog Platform() => MigrationCatalog.Platform;

    public static MigrationScript Script(string identity, string sql) => new(identity, sql);

    public static MigrationCatalog With(MigrationCatalog catalog, params MigrationScript[] scripts) =>
        MigrationCatalog.Create(catalog.Scripts.Concat(scripts));

    public static MigrationCatalog With(params string[] scenarioFolders)
    {
        var assembly = typeof(TestMigrationCatalogs).Assembly;
        var scripts = new List<MigrationScript>(MigrationCatalog.Platform.Scripts);
        foreach (var folder in scenarioFolders)
        {
            var prefix = ResourcePrefix + folder + ".";
            scripts.AddRange(MigrationCatalog.FromEmbeddedResources(assembly, prefix).Scripts);
        }

        return MigrationCatalog.Create(scripts);
    }
}
