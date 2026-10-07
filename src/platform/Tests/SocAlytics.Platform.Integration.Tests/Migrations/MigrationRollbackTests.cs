using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationRollbackTests(PostgresContainerFixture postgres)
{
    private static async Task<object?> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    [Fact]
    public async Task FailingScriptRollsBackAndLaterScriptsAreNotAttempted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var platformOnly = TestMigrationCatalogs.Platform();
        (await MigratorHarness.RunAsync(db, platformOnly, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var catalog = TestMigrationCatalogs.With(
            TestMigrationCatalogs.With("Failing"),
            TestMigrationCatalogs.Script("9002_test_after", "CREATE TABLE socalytics.test_after (id integer);"));
        var result = await MigratorHarness.RunAsync(db, catalog, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.MigrationFailed);
        var failure = result.Logs.Single(e => e.EventId == 1100);
        failure.State["Category"].ShouldBe("migration-failed");
        failure.State["Identity"].ShouldBe("9001_test_fails_midway");
        failure.State["SqlState"].ShouldBe("P0001");

        (await ScalarAsync(db, "SELECT to_regclass('socalytics.test_failing')::text", ct)).ShouldBe(DBNull.Value);
        (await ScalarAsync(db, "SELECT to_regclass('socalytics.test_after')::text", ct)).ShouldBe(DBNull.Value);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics_migrations.history WHERE sequence >= 9001", ct))
            .ShouldBe(0L);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics_migrations.history", ct))
            .ShouldBe((long)platformOnly.Scripts.Count);
    }

    [Fact]
    public async Task FailingFirstScriptLeavesNoHistoryTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var catalog = MigrationCatalogFor(
            TestMigrationCatalogs.Script("0001_test_broken_first", "SELECT 1/0;"));

        var result = await MigratorHarness.RunAsync(db, catalog, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.MigrationFailed);
        (await ScalarAsync(db, "SELECT to_regclass('socalytics_migrations.history')::text", ct))
            .ShouldBe(DBNull.Value);
    }

    private static SocAlytics.Platform.Infrastructure.Persistence.Migrations.MigrationCatalog MigrationCatalogFor(
        params SocAlytics.Platform.Infrastructure.Persistence.Migrations.MigrationScript[] scripts) =>
        SocAlytics.Platform.Infrastructure.Persistence.Migrations.MigrationCatalog.Create(scripts);
}
