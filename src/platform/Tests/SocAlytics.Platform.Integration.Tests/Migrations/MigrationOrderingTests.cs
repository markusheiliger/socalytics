using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationOrderingTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task AppliesOutOfOrderRegisteredScriptsInAscendingOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var catalog = TestMigrationCatalogs.With(
            TestMigrationCatalogs.Platform(),
            TestMigrationCatalogs.Script("9003_test_third", "CREATE TABLE socalytics.t_third (id integer);"),
            TestMigrationCatalogs.Script("9001_test_first", "CREATE TABLE socalytics.t_first (id integer);"),
            TestMigrationCatalogs.Script("9002_test_second", "CREATE TABLE socalytics.t_second (id integer);"));

        var result = await MigratorHarness.RunAsync(db, catalog, ct);

        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        var applying = result.Logs.Where(e => e.EventId == 1004).Select(e => e.State["Identity"]).ToArray();
        applying.ShouldBe(catalog.Scripts.Select(s => s.Identity).ToArray());

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT sequence, identity, checksum, applied_at FROM socalytics_migrations.history ORDER BY applied_at, sequence",
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(int Sequence, string Identity, string Checksum)>();
        while (await reader.ReadAsync(ct))
        {
            reader.GetFieldValue<DateTimeOffset>(3).ShouldNotBe(default);
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        rows.Count.ShouldBe(catalog.Scripts.Count);
        foreach (var script in catalog.Scripts)
        {
            rows.ShouldContain((script.Sequence, script.Identity, script.Checksum));
        }
    }
}
