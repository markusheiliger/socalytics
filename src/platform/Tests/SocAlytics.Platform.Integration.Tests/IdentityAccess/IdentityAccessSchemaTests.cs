using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class IdentityAccessSchemaTests(PostgresContainerFixture postgres)
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

    private static async Task<Guid> InsertTeamAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var season = Guid.NewGuid();
        await ExecAsync(c, $"INSERT INTO socalytics.season (id, name, created_at) VALUES ('{season}', 'S', {Now})", ct);
        var team = Guid.NewGuid();
        await ExecAsync(c, $"INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES ('{team}', '{season}', 'T', {Now})", ct);
        return team;
    }

    private static async Task<long> AccountVersionAsync(NpgsqlConnection c, Guid id, CancellationToken ct) =>
        (long)(await ExecAsync(c, $"SELECT version FROM socalytics.member_account WHERE id = '{id}'", ct))!;

    private static async Task<Guid> InsertSessionAsync(NpgsqlConnection c, Guid account, string reason, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(c,
            $"INSERT INTO socalytics.member_session (id, member_account_id, token_hash, security_stamp, created_at, last_seen_at, idle_expires_at, absolute_expires_at, ended_at, end_reason) " +
            $"VALUES ('{id}', '{account}', decode('{Guid.NewGuid():N}', 'hex'), 's', {Now}, {Now}, {Now}, {Now}, {Now}, {reason})", ct);
        return id;
    }

    [Fact]
    public async Task ClubRoleAssignmentChangesAdvanceAccountVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        var v = await AccountVersionAsync(c, account, ct);

        await ExecAsync(c, $"INSERT INTO socalytics.club_role_assignment (member_account_id, role, assigned_at) VALUES ('{account}', 'registrar', {Now})", ct);
        var afterInsert = await AccountVersionAsync(c, account, ct);
        afterInsert.ShouldBeGreaterThan(v);

        await ExecAsync(c, $"DELETE FROM socalytics.club_role_assignment WHERE member_account_id = '{account}'", ct);
        (await AccountVersionAsync(c, account, ct)).ShouldBeGreaterThan(afterInsert);
    }

    [Fact]
    public async Task TeamRoleAssignmentChangesAdvanceAccountVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        var team = await InsertTeamAsync(c, ct);
        var v = await AccountVersionAsync(c, account, ct);

        await ExecAsync(c, $"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{account}', '{team}', 'coach', {Now}, '{account}')", ct);
        var afterInsert = await AccountVersionAsync(c, account, ct);
        afterInsert.ShouldBeGreaterThan(v);

        await ExecAsync(c, $"DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{account}'", ct);
        (await AccountVersionAsync(c, account, ct)).ShouldBeGreaterThan(afterInsert);
    }

    [Fact]
    public async Task SecondRoleForSameTeamViolatesPrimaryKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        var team = await InsertTeamAsync(c, ct);
        await ExecAsync(c, $"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{account}', '{team}', 'coach', {Now}, '{account}')", ct);

        (await SqlStateAsync(c,
            $"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{account}', '{team}', 'viewer', {Now}, '{account}')", ct))
            .ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task RecoveryDirectiveUseIsAppendOnlyForRuntimeRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        await ExecAsync(c, $"INSERT INTO socalytics.recovery_directive_use (recovery_id, member_account_id, applied_at, correlation_id) VALUES ('recovery-1', '{account}', {Now}, 'c')", ct);
        (await ExecAsync(c, "SELECT count(*) FROM socalytics.recovery_directive_use", ct)).ShouldBe(1L);

        foreach (var sql in new[]
        {
            "UPDATE socalytics.recovery_directive_use SET correlation_id = 'x'",
            "DELETE FROM socalytics.recovery_directive_use",
            "TRUNCATE socalytics.recovery_directive_use",
        })
        {
            (await SqlStateAsync(c, sql, ct)).ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task SessionEndReasonCheckRejectsInvalidValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);
        await InsertSessionAsync(c, account, "'sign-out'", ct);

        var ex = await Should.ThrowAsync<PostgresException>(() => InsertSessionAsync(c, account, "'bogus'", ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("now()", "NULL")]
    [InlineData("NULL", "'superseded'")]
    public async Task CredentialRevocationCheckRejectsInconsistentRows(string revokedAt, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var account = await InsertAccountAsync(c, "alice", ct);

        (await SqlStateAsync(c,
            $"INSERT INTO socalytics.one_time_credential (id, member_account_id, purpose, credential_hash, issued_at, issued_by_account_id, expires_at, revoked_at, revocation_reason) " +
            $"VALUES ('{Guid.NewGuid()}', '{account}', 'set-password', decode('{Guid.NewGuid():N}', 'hex'), {Now}, '{account}', {Now}, {revokedAt}, {reason})", ct))
            .ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
