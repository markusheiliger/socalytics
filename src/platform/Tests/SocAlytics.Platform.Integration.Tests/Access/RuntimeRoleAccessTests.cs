using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Access;

public sealed class RuntimeRoleAccessTests(PostgresContainerFixture postgres)
{
    private const string Structure = """
        SELECT coalesce(string_agg(n.nspname || '.' || c.relname || ':' || c.relkind::text, ',' ORDER BY n.nspname, c.relname), '')
             || '|' || (SELECT coalesce(string_agg(nspname, ',' ORDER BY nspname), '') FROM pg_namespace)
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
        """;

    private const string HistoryContent =
        "SELECT string_agg(sequence || identity || checksum, ',' ORDER BY sequence) FROM socalytics_migrations.history";

    private static readonly string ZeroChecksum = "sha-256:" + new string('0', 64);

    public static TheoryData<string> ForbiddenStatements =>
    [
        "CREATE TABLE socalytics.x (id integer)",
        "CREATE TABLE public.x (id integer)",
        "CREATE SCHEMA x",
        "CREATE TEMP TABLE x (id integer)",
        "ALTER SCHEMA socalytics RENAME TO socalytics_renamed",
        $"INSERT INTO socalytics_migrations.history (sequence, identity, checksum) VALUES (9999, 'x', '{ZeroChecksum}')",
        "UPDATE socalytics_migrations.history SET identity = identity || 'x'",
        "DELETE FROM socalytics_migrations.history",
    ];

    private async Task<IsolatedDatabase> CreateMigratedDatabaseAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    [Theory]
    [MemberData(nameof(ForbiddenStatements))]
    public async Task AppRoleCannotChangeStructureOrHistory(string statement)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateMigratedDatabaseAsync(ct);

        await using var admin = await OpenAsync(db.MigratorConnectionString, ct);
        var structureBefore = await ScalarAsync(admin, Structure, ct);
        var historyBefore = await ScalarAsync(admin, HistoryContent, ct);

        await using (var app = await OpenAsync(db.AppConnectionString, ct))
        {
            await using var command = new NpgsqlCommand(statement, app);
            var exception = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
            exception.SqlState.ShouldBe("42501");
        }

        (await ScalarAsync(admin, Structure, ct)).ShouldBe(structureBefore);
        (await ScalarAsync(admin, HistoryContent, ct)).ShouldBe(historyBefore);
    }

    [Fact]
    public async Task AppRoleCanReadHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateMigratedDatabaseAsync(ct);

        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var count = (long)(await ScalarAsync(app, "SELECT count(*) FROM socalytics_migrations.history", ct))!;
        count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task MigratorRoleOwnsSchemasAndHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateMigratedDatabaseAsync(ct);

        await using var connection = await OpenAsync(db.AppConnectionString, ct);
        foreach (var sql in new[]
        {
            "SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'socalytics'",
            "SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'socalytics_migrations'",
            "SELECT relowner::regrole::text FROM pg_class WHERE oid = 'socalytics_migrations.history'::regclass",
        })
        {
            (await ScalarAsync(connection, sql, ct)).ShouldBe("socalytics_migrator", sql);
        }
    }

    [Fact]
    public async Task TablesCreatedLaterByMigratorAreUsableByAppRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateMigratedDatabaseAsync(ct);

        await using (var migrator = await OpenAsync(db.MigratorConnectionString, ct))
        {
            await using var create = new NpgsqlCommand(
                "SET ROLE socalytics_migrator; CREATE TABLE socalytics.later_table (id integer PRIMARY KEY, name text)", migrator);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var app = await OpenAsync(db.AppConnectionString, ct);
        foreach (var sql in new[]
        {
            "INSERT INTO socalytics.later_table (id, name) VALUES (1, 'a')",
            "UPDATE socalytics.later_table SET name = 'b' WHERE id = 1",
        })
        {
            await using var command = new NpgsqlCommand(sql, app);
            (await command.ExecuteNonQueryAsync(ct)).ShouldBe(1);
        }

        (await ScalarAsync(app, "SELECT name FROM socalytics.later_table WHERE id = 1", ct)).ShouldBe("b");

        await using var delete = new NpgsqlCommand("DELETE FROM socalytics.later_table", app);
        (await delete.ExecuteNonQueryAsync(ct)).ShouldBe(1);
    }
}
