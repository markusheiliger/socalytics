using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class UnknownAppliedMigrationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task UnknownAppliedRowIsLeftUntouchedAndWarnedAbout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var newer = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", "CREATE TABLE socalytics.test_a (id integer);"),
            TestMigrationCatalogs.Script("9002_test_b", "CREATE TABLE socalytics.test_b (id integer);"));
        (await MigratorHarness.RunAsync(db, newer, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var before = await HistoryRowAsync(db, ct);

        var older = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", "CREATE TABLE socalytics.test_a (id integer);"));
        var result = await MigratorHarness.RunAsync(db, older, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        var warning = result.Logs.Single(e => e.EventId == 1003);
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("9002_test_b");
        (await HistoryRowAsync(db, ct)).ShouldBe(before);
    }

    private static async Task<string> HistoryRowAsync(IsolatedDatabase db, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT sequence || identity || checksum || applied_at::text FROM socalytics_migrations.history WHERE identity = '9002_test_b'",
            connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
}
