using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationHistoryStoreTests(PostgresContainerFixture postgres)
{
    private static readonly string Checksum = "sha-256:" + new string('a', 64);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string, object)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task CreateHistoryAsync(NpgsqlConnection migrator)
    {
        await ExecuteAsync(migrator, MigrationHistorySql.CreateSchema);
        await ExecuteAsync(migrator, MigrationHistorySql.CreateTable);
        await ExecuteAsync(migrator, MigrationHistorySql.GrantSchemaUsage);
        await ExecuteAsync(migrator, MigrationHistorySql.GrantSelect);
    }

    private static Task InsertAsync(NpgsqlConnection connection, int sequence, string identity, string checksum) =>
        ExecuteAsync(connection, MigrationHistorySql.Insert,
            ("sequence", sequence), ("identity", identity), ("checksum", checksum));

    private static async Task<string?> SqlStateOfAsync(Func<Task> action)
    {
        var ex = await Should.ThrowAsync<PostgresException>(action);
        return ex.SqlState;
    }

    [Fact]
    public async Task MissingTableReturnsEmpty()
    {
        await using var db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);

        var entries = await MigrationHistoryStore.ReadEntriesAsync(app, null, TestContext.Current.CancellationToken);

        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task InsertedRowsAreReadableWithAppConnection()
    {
        await using var db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var migrator = new NpgsqlConnection(db.MigratorConnectionString);
        await migrator.OpenAsync(TestContext.Current.CancellationToken);
        await CreateHistoryAsync(migrator);
        await InsertAsync(migrator, 1, "0001_foundation_one", Checksum);

        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        var entries = await MigrationHistoryStore.ReadEntriesAsync(app, null, TestContext.Current.CancellationToken);

        entries.ShouldBe([new MigrationHistoryEntry(1, "0001_foundation_one", Checksum)]);
    }

    [Fact]
    public async Task ConstraintsRejectInvalidAndDuplicateRows()
    {
        await using var db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var migrator = new NpgsqlConnection(db.MigratorConnectionString);
        await migrator.OpenAsync(TestContext.Current.CancellationToken);
        await CreateHistoryAsync(migrator);

        (await SqlStateOfAsync(() => InsertAsync(migrator, 0, "0000_foundation_zero", Checksum))).ShouldBe("23514");
        (await SqlStateOfAsync(() => InsertAsync(migrator, 1, "0001_foundation_one", "sha-256:xyz"))).ShouldBe("23514");

        await InsertAsync(migrator, 1, "0001_foundation_one", Checksum);
        (await SqlStateOfAsync(() => InsertAsync(migrator, 2, "0001_foundation_one", Checksum))).ShouldBe("23505");
        (await SqlStateOfAsync(() => InsertAsync(migrator, 1, "0001_foundation_other", Checksum))).ShouldBe("23505");
    }

    [Fact]
    public async Task AppConnectionCannotInsert()
    {
        await using var db = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var migrator = new NpgsqlConnection(db.MigratorConnectionString);
        await migrator.OpenAsync(TestContext.Current.CancellationToken);
        await CreateHistoryAsync(migrator);

        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);

        (await SqlStateOfAsync(() => InsertAsync(app, 1, "0001_foundation_one", Checksum))).ShouldBe("42501");
    }
}
