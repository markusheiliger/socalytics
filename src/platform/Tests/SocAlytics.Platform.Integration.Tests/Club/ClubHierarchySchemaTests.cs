using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Club;

public sealed class ClubHierarchySchemaTests(PostgresContainerFixture postgres)
{
    private const string Now = "now()";

    private async Task<NpgsqlConnection> OpenMigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<object?> ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<string?> SqlStateAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var ex = await Should.ThrowAsync<PostgresException>(() => ExecAsync(connection, sql, ct));
        return ex.SqlState;
    }

    private static async Task<Guid> InsertAccountAsync(NpgsqlConnection c, string name, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(c,
            $"INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, membership_changed_at, created_at) " +
            $"VALUES ('{id}', '{name}', '{name.ToUpperInvariant()}', 's', {Now}, {Now})", ct);
        return id;
    }

    private static async Task<Guid> InsertSeasonAsync(NpgsqlConnection c, string state, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var activated = state == "draft" ? "NULL" : Now;
        var archived = state == "archived" ? Now : "NULL";
        await ExecAsync(c,
            $"INSERT INTO socalytics.season (id, name, state, created_at, activated_at, archived_at) VALUES ('{id}', 'S', '{state}', {Now}, {activated}, {archived})", ct);
        return id;
    }

    private static async Task<long> VersionAsync(NpgsqlConnection c, string table, Guid id, CancellationToken ct) =>
        (long)(await ExecAsync(c, $"SELECT version FROM socalytics.{table} WHERE id = '{id}'", ct))!;

    [Fact]
    // Quickstart A36
    public async Task SecondClubRowViolatesUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var admin = await InsertAccountAsync(c, "admin", ct);
        await ExecAsync(c, $"INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) VALUES ('{Guid.NewGuid()}', 'C', '{admin}', {Now})", ct);

        (await SqlStateAsync(c,
            $"INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) VALUES ('{Guid.NewGuid()}', 'D', '{admin}', {Now})", ct))
            .ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task SecondActiveSeasonViolatesUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        await InsertSeasonAsync(c, "active", ct);

        var ex = await Should.ThrowAsync<PostgresException>(() => InsertSeasonAsync(c, "active", ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData("draft", "now()", "NULL")]
    [InlineData("draft", "NULL", "now()")]
    [InlineData("active", "NULL", "NULL")]
    [InlineData("active", "now()", "now()")]
    [InlineData("archived", "now()", "NULL")]
    [InlineData("archived", "NULL", "now()")]
    public async Task InconsistentSeasonTimestampsAreRejected(string state, string activated, string archived)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);

        (await SqlStateAsync(c,
            $"INSERT INTO socalytics.season (id, name, state, created_at, activated_at, archived_at) VALUES ('{Guid.NewGuid()}', 'S', '{state}', {Now}, {activated}, {archived})", ct))
            .ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task DuplicateNormalizedAccountNameViolatesUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        await InsertAccountAsync(c, "alice", ct);

        var ex = await Should.ThrowAsync<PostgresException>(() => InsertAccountAsync(c, "alice", ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task UpdatingEachRootAdvancesVersionByOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        var clubId = Guid.NewGuid();
        await ExecAsync(c, $"INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) VALUES ('{clubId}', 'C', '{account}', {Now})", ct);
        var season = await InsertSeasonAsync(c, "draft", ct);
        var team = Guid.NewGuid();
        await ExecAsync(c, $"INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES ('{team}', '{season}', 'T', {Now})", ct);
        var match = Guid.NewGuid();
        await ExecAsync(c,
            $"INSERT INTO socalytics.match (id, team_id, opponent_name, kickoff_at, home_away, created_at, created_by_account_id) VALUES ('{match}', '{team}', 'O', {Now}, 'home', {Now}, '{account}')", ct);

        var roots = new (string Table, Guid Id, string Set)[]
        {
            ("member_account", account, "password_hash = 'h'"),
            ("club", clubId, "display_name = 'C2'"),
            ("season", season, "name = 'S2'"),
            ("team", team, "name = 'T2'"),
            ("match", match, "opponent_name = 'O2'"),
        };

        foreach (var (table, id, set) in roots)
        {
            (await VersionAsync(c, table, id, ct)).ShouldBe(1);
            await ExecAsync(c, $"UPDATE socalytics.{table} SET {set} WHERE id = '{id}'", ct);
            (await VersionAsync(c, table, id, ct)).ShouldBe(2, table);
        }
    }
}
