using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Concurrency;

public sealed class VersionAdvancementTests(PostgresContainerFixture postgres)
{
    private async Task<IsolatedDatabase> CreateDatabaseAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, TestMigrationCatalogs.With("Versioning"), ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<object?> ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<long> VersionAsync(NpgsqlConnection connection, Guid id, CancellationToken ct) =>
        (long)(await ExecAsync(connection, $"SELECT version FROM socalytics.test_widget WHERE id = '{id}'", ct))!;

    private static async Task<Guid> InsertWidgetAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(connection, $"INSERT INTO socalytics.test_widget (id, name) VALUES ('{id}', 'a')", ct);
        return id;
    }

    [Fact]
    public async Task ChangingUpdateAdvancesByExactlyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var id = await InsertWidgetAsync(app, ct);

        await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'b' WHERE id = '{id}'", ct);
        (await VersionAsync(app, id, ct)).ShouldBe(2);

        await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'b' WHERE id = '{id}'", ct);
        (await VersionAsync(app, id, ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("version = 42")]
    [InlineData("version = version + 1")]
    public async Task UpdateWritingVersionStillEndsAtOldPlusOne(string versionAssignment)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var id = await InsertWidgetAsync(app, ct);

        await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'c', {versionAssignment} WHERE id = '{id}'", ct);
        (await VersionAsync(app, id, ct)).ShouldBe(2);
    }

    [Fact]
    public async Task TwoUpdatesInOneTransactionAdvanceByTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var id = await InsertWidgetAsync(app, ct);

        await using (var tx = await app.BeginTransactionAsync(ct))
        {
            await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'b' WHERE id = '{id}'", ct, tx);
            await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'c' WHERE id = '{id}'", ct, tx);
            await tx.CommitAsync(ct);
        }

        (await VersionAsync(app, id, ct)).ShouldBe(3);
    }

    [Fact]
    public async Task PartChangesAdvanceTheRootExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var widget = await InsertWidgetAsync(app, ct);
        var other = await InsertWidgetAsync(app, ct);
        var part = Guid.NewGuid();

        await ExecAsync(app, $"INSERT INTO socalytics.test_widget_part (id, widget_id, label) VALUES ('{part}', '{widget}', 'x')", ct);
        (await VersionAsync(app, widget, ct)).ShouldBe(2);

        await ExecAsync(app, $"UPDATE socalytics.test_widget_part SET label = 'y' WHERE id = '{part}'", ct);
        (await VersionAsync(app, widget, ct)).ShouldBe(3);

        await ExecAsync(app, $"UPDATE socalytics.test_widget_part SET label = 'y' WHERE id = '{part}'", ct);
        (await VersionAsync(app, widget, ct)).ShouldBe(3);

        await ExecAsync(app, $"UPDATE socalytics.test_widget_part SET widget_id = '{other}' WHERE id = '{part}'", ct);
        (await VersionAsync(app, widget, ct)).ShouldBe(4);
        (await VersionAsync(app, other, ct)).ShouldBe(2);

        await ExecAsync(app, $"DELETE FROM socalytics.test_widget_part WHERE id = '{part}'", ct);
        (await VersionAsync(app, other, ct)).ShouldBe(3);
    }

    [Fact]
    public async Task AppRoleCannotSuppressVersionAdvancement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);
        var id = await InsertWidgetAsync(app, ct);

        await using (var tx = await app.BeginTransactionAsync(ct))
        {
            await ExecAsync(app, "SET LOCAL socalytics.suppress_version = 'on'", ct, tx);
            await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'b' WHERE id = '{id}'", ct, tx);
            await tx.CommitAsync(ct);
        }

        (await VersionAsync(app, id, ct)).ShouldBe(2);
    }

    [Fact]
    public async Task TriggersExistAndAppRoleCannotCallAttachProcedures()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var app = await OpenAsync(db.AppConnectionString, ct);

        // Only the test aggregate's tables: product migrations attach version triggers to their own tables.
        var triggers = (string)(await ExecAsync(app,
            "SELECT string_agg(tgname, ',' ORDER BY tgname) FROM pg_trigger WHERE NOT tgisinternal"
            + " AND tgrelid IN ('socalytics.test_widget'::regclass, 'socalytics.test_widget_part'::regclass)", ct))!;
        triggers.ShouldBe("test_widget_part_root_touch,test_widget_part_root_touch_update,test_widget_version_advance");

        var firstArgument = (string)(await ExecAsync(app,
            "SELECT convert_from(decode(split_part(encode(tgargs, 'hex'), '00', 1), 'hex'), 'UTF8') FROM pg_trigger WHERE tgname = 'test_widget_part_root_touch'", ct))!;
        firstArgument.ShouldBe("test_widget");

        foreach (var call in new[]
        {
            "CALL socalytics.attach_version_trigger('socalytics.test_widget')",
            "CALL socalytics.attach_aggregate_child_triggers('socalytics.test_widget_part', 'socalytics.test_widget', 'widget_id')",
        })
        {
            await using var command = new NpgsqlCommand(call, app);
            var exception = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
            exception.SqlState.ShouldBe("42501");
        }
    }
}
