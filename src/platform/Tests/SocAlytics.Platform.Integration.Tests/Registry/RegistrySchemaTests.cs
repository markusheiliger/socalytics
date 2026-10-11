using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistrySchemaTests(PostgresContainerFixture postgres)
{
    private async Task<NpgsqlConnection> OpenMigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);
        var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<object?> ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, c);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<string?> SqlStateAsync(NpgsqlConnection c, string sql, CancellationToken ct) =>
        (await Should.ThrowAsync<PostgresException>(() => ExecAsync(c, sql, ct))).SqlState;

    private static string InsertRegistration(Guid id, string thumbprint, string state, string? handle = null) =>
        "INSERT INTO socalytics.analyst_manager_registration (id, stamp_id, state, public_jwk, key_thumbprint, device_fingerprint, " +
        "pairing_nonce, device_metadata, polling_handle_hash, pairing_expires_at, correlation_id, created_at) " +
        $"VALUES ('{id}', 'stamp', '{state}', '{{}}', '{thumbprint}', 'ABCD-EFGH', 'nonce-0123456789ab', '{{}}', " +
        $"decode('{handle ?? Guid.NewGuid().ToString("N")}', 'hex'), now(), 'corr', now())";

    private static string InsertAudit(string actorKind) =>
        "INSERT INTO socalytics.security_audit_event (id, occurred_at, event_type, action, outcome, actor_kind, resource_type, correlation_id) " +
        $"VALUES ('{Guid.NewGuid()}', now(), 'e', 'a', 'succeeded', '{actorKind}', 'r', 'c')";

    [Fact]
    public async Task TablesAndIndexesExist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var tables = (long)(await ExecAsync(c,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'socalytics' AND table_name IN " +
            "('analyst_manager_registration', 'analyst_manager_access_token', 'analyst_manager_proof_replay')", ct))!;
        tables.ShouldBe(3);
        var indexes = (long)(await ExecAsync(c,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'socalytics' AND indexname IN " +
            "('ux_analyst_manager_registration_active_thumbprint', 'ix_analyst_manager_access_token_registration', " +
            "'ix_analyst_manager_access_token_expires', 'ix_analyst_manager_proof_replay_expires')", ct))!;
        indexes.ShouldBe(4);
    }

    [Fact]
    public async Task VersionAdvancesOnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var id = Guid.NewGuid();
        await ExecAsync(c, InsertRegistration(id, "t1", "pairing"), ct);
        await ExecAsync(c, $"UPDATE socalytics.analyst_manager_registration SET state = 'pending_approval' WHERE id = '{id}'", ct);
        (await ExecAsync(c, $"SELECT version FROM socalytics.analyst_manager_registration WHERE id = '{id}'", ct)).ShouldBe(2L);
    }

    [Fact]
    public async Task InvalidStateKindAndScopeAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        (await SqlStateAsync(c, InsertRegistration(Guid.NewGuid(), "t2", "bogus"), ct)).ShouldBe("23514");
        (await SqlStateAsync(c,
            "INSERT INTO socalytics.analyst_manager_proof_replay (kind, jti_hash, expires_at) VALUES ('other', decode('00', 'hex'), now())", ct))
            .ShouldBe("23514");
        var id = Guid.NewGuid();
        await ExecAsync(c, InsertRegistration(id, "t3", "pairing"), ct);
        (await SqlStateAsync(c,
            "INSERT INTO socalytics.analyst_manager_access_token (token_hash, registration_id, jkt, scope, issued_at, expires_at) " +
            $"VALUES (decode('01', 'hex'), '{id}', 'j', 'other', now(), now())", ct)).ShouldBe("23514");
    }

    [Fact]
    public async Task NonTerminalThumbprintIsUniqueButTerminalIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        await ExecAsync(c, InsertRegistration(Guid.NewGuid(), "same", "active"), ct);
        (await SqlStateAsync(c, InsertRegistration(Guid.NewGuid(), "same", "pairing"), ct)).ShouldBe("23505");
        await ExecAsync(c, InsertRegistration(Guid.NewGuid(), "same", "revoked"), ct);
        await ExecAsync(c, InsertRegistration(Guid.NewGuid(), "same", "expired"), ct);
    }

    [Theory]
    [InlineData("analyst-manager")]
    [InlineData("member")]
    [InlineData("system")]
    [InlineData("anonymous")]
    public async Task AllowedAuditActorKindsAreAccepted(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        await ExecAsync(c, InsertAudit(kind), ct);
    }

    [Fact]
    public async Task UnknownAuditActorKindIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await OpenMigratedAsync(ct);
        var ex = await Should.ThrowAsync<PostgresException>(() => ExecAsync(c, InsertAudit("robot"), ct));
        ex.ConstraintName.ShouldBe("ck_security_audit_event_actor_kind");
    }
}
