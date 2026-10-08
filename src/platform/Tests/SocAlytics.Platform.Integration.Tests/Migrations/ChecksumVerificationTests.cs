using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class ChecksumVerificationTests(PostgresContainerFixture postgres)
{
    private const string OriginalSql = "CREATE TABLE socalytics.test_a (id integer);\nSELECT 1;\n";

    private static async Task<object?> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    [Fact]
    public async Task ChangedAppliedScriptFailsBeforeApplyingAnythingAndDoesNotLogScriptText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var first = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", OriginalSql));
        (await MigratorHarness.RunAsync(db, first, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        const string changedSql = "CREATE TABLE socalytics.test_a (id integer, secret_marker text);";
        var second = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", changedSql),
            TestMigrationCatalogs.Script("9002_test_b", "CREATE TABLE socalytics.test_b (id integer);"));
        var result = await MigratorHarness.RunAsync(db, second, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.ChecksumMismatch);
        (await ScalarAsync(db, "SELECT to_regclass('socalytics.test_b')::text", ct)).ShouldBe(DBNull.Value);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics_migrations.history WHERE sequence = 9002", ct))
            .ShouldBe(0L);

        var failure = result.Logs.Single(e => e.EventId == 1100);
        failure.State["Category"].ShouldBe("checksum-mismatch");
        failure.State["Identity"].ShouldBe("9001_test_a");
        result.Logs.ShouldAllBe(e => !e.Message.Contains("secret_marker") && (e.Exception == null || !e.Exception.Contains("secret_marker")));
    }

    [Fact]
    public async Task LineEndingOnlyChangeIsNotAMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var first = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", OriginalSql));
        (await MigratorHarness.RunAsync(db, first, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var second = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", OriginalSql.Replace("\n", "\r\n", StringComparison.Ordinal)));
        var result = await MigratorHarness.RunAsync(db, second, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        result.Logs.Single(e => e.EventId == 1006).Message.ShouldContain("0 applied");
    }
}
