using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Structure;

public sealed class PersistenceStructureTests(PostgresContainerFixture postgres)
{
    private async Task<IsolatedDatabase> MigrateAsync(MigrationCatalog catalog, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, catalog, ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    [Fact]
    public async Task PlatformCatalogHasNoStructureViolations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigrateAsync(MigrationCatalog.Platform, ct);

        var violations = await PersistenceStructureChecker.CheckAsync(
            db.MigratorConnectionString, PersistedTableClassifications.Platform, ct);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task ChildTableDeclaredInManifestHasNoViolations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigrateAsync(TestMigrationCatalogs.With("Versioning"), ct);
        var manifest = new Dictionary<string, TableClassification>(PersistedTableClassifications.Platform)
        {
            ["test_widget_part"] = TableClassification.ChildOf("test_widget", "widget_id"),
        };

        var violations = await PersistenceStructureChecker.CheckAsync(db.MigratorConnectionString, manifest, ct);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task FoundationMigrationsCreateNoTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigrateAsync(
            MigrationCatalog.Create(MigrationCatalog.Platform.Scripts.Where(script => script.Area == "foundation")), ct);

        (await PersistenceStructureChecker.CountTablesAsync(db.MigratorConnectionString, ct)).ShouldBe(0);
        var violations = await PersistenceStructureChecker.CheckAsync(
            db.MigratorConnectionString, new Dictionary<string, TableClassification>(), ct);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task CheckerReportsEachDeliberateViolation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigrateAsync(TestMigrationCatalogs.With("StructureViolations"), ct);
        var manifest = new Dictionary<string, TableClassification>
        {
            ["bad_child_no_touch"] = TableClassification.ChildOf("bad_unadvanced", "unadvanced_id"),
            ["bad_child_versioned"] = TableClassification.ChildOf("bad_unadvanced", "unadvanced_id"),
            ["bad_discriminator"] = TableClassification.Immutable,
            ["missing_table"] = TableClassification.Unversioned("test"),
        };

        var violations = await PersistenceStructureChecker.CheckAsync(db.MigratorConnectionString, manifest, ct);

        bool Has(StructureViolationKind kind, string subject) =>
            violations.Any(v => v.Kind == kind && v.Subject.Contains(subject, StringComparison.Ordinal));

        Has(StructureViolationKind.MissingAdvanceTrigger, "bad_unadvanced").ShouldBeTrue();
        Has(StructureViolationKind.MissingTouchTriggers, "bad_child_no_touch").ShouldBeTrue();
        Has(StructureViolationKind.ChildHasVersionColumn, "bad_child_versioned").ShouldBeTrue();
        Has(StructureViolationKind.ForbiddenName, "bad_discriminator").ShouldBeTrue();
        Has(StructureViolationKind.UnknownManifestEntry, "missing_table").ShouldBeTrue();
    }
}
