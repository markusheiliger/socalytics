using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class RepeatRunTests(PostgresContainerFixture postgres)
{
    private const string Snapshot = """
        SELECT coalesce(string_agg(c.relname || ':' || c.relkind::text, ',' ORDER BY c.relname), '')
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'socalytics'
        """;

    private const string HistoryRows =
        "SELECT coalesce(string_agg(sequence || identity || checksum || applied_at::text, ',' ORDER BY sequence), '') FROM socalytics_migrations.history";

    private static async Task<string> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task FurtherRunsApplyNothingAndChangeNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var catalog = TestMigrationCatalogs.Platform();

        (await MigratorHarness.RunAsync(db, catalog, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var history = await ScalarAsync(db, HistoryRows, ct);
        var schema = await ScalarAsync(db, Snapshot, ct);

        for (var i = 0; i < 3; i++)
        {
            var result = await MigratorHarness.RunAsync(db, catalog, ct);
            result.ExitCode.ShouldBe(MigratorExitCode.Success);
            result.Logs.Single(e => e.EventId == 1006).Message.ShouldContain("0 applied");
            (await ScalarAsync(db, HistoryRows, ct)).ShouldBe(history);
            (await ScalarAsync(db, Snapshot, ct)).ShouldBe(schema);
        }
    }
}
