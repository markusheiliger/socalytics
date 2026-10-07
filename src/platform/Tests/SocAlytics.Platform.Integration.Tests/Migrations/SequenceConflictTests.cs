using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class SequenceConflictTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task PendingScriptBelowHighestAppliedSequenceFailsWithoutApplyingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var first = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", "CREATE TABLE socalytics.test_a (id integer);"));
        (await MigratorHarness.RunAsync(db, first, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var second = TestMigrationCatalogs.With(
            first,
            TestMigrationCatalogs.Script("0500_test_late", "CREATE TABLE socalytics.test_late (id integer);"));
        var result = await MigratorHarness.RunAsync(db, second, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.SequenceConflict);
        result.Logs.Single(e => e.EventId == 1100).State["Identity"].ShouldBe("0500_test_late");
        await AssertNothingAppliedAsync(db, ct);
    }

    [Fact]
    public async Task PendingScriptSharingSequenceWithUnknownAppliedRowIsASequenceConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var first = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", "CREATE TABLE socalytics.test_a (id integer);"),
            TestMigrationCatalogs.Script("9002_test_b", "CREATE TABLE socalytics.test_b (id integer);"));
        (await MigratorHarness.RunAsync(db, first, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var second = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9001_test_a", "CREATE TABLE socalytics.test_a (id integer);"),
            TestMigrationCatalogs.Script("9002_test_c", "CREATE TABLE socalytics.test_c (id integer);"));
        var result = await MigratorHarness.RunAsync(db, second, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.SequenceConflict);
        result.Logs.Single(e => e.EventId == 1100).State["Identity"].ShouldBe("9002_test_c");
    }

    private static async Task AssertNothingAppliedAsync(IsolatedDatabase db, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('socalytics.test_late') IS NULL AND NOT EXISTS (SELECT 1 FROM socalytics_migrations.history WHERE sequence = 500)",
            connection);
        ((bool)(await command.ExecuteScalarAsync(ct))!).ShouldBeTrue();
    }
}
