using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RevocationFencingTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host, RegisteredAnalystManagerClient Manager)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Real);
        await host.StartAsync(ct);
        var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);
        return (db, host, manager);
    }

    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> RegistrationIdAsync(IsolatedDatabase db, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id FROM socalytics.analyst_manager_registration", c);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<(Guid Attempt, long Token)> ClaimAsync(RegisteredAnalystManagerClient m, Guid job, Guid occ, string key, CancellationToken ct)
    {
        using var response = await m.ClaimAsync(job, occ, key, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return (body.GetProperty("attemptId").GetGuid(), body.GetProperty("fencingToken").GetInt64());
    }

    private static async Task ClaimForOtherManagerAsync(IsolatedDatabase db, Guid job, Guid occ, CancellationToken ct)
    {
        var attempt = Guid.NewGuid();
        await ExecAsync(db,
            $"""
            INSERT INTO socalytics.analysis_execution_attempts
                (id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
            VALUES ('{attempt}', '{job}', '{occ}', 1, '{Guid.NewGuid()}', nextval('socalytics.analysis_fencing_token_seq'), 'Active', now(), now() + interval '1 day', now());
            UPDATE socalytics.analysis_logical_jobs SET state = 'Claimed', attempts_used = 1, current_attempt_id = '{attempt}' WHERE id = '{job}';
            """, ct);
    }

    [Fact]
    public async Task RevocationFencesEveryActiveAttemptOfTheManagerOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host, manager) = await StartAsync(ct);
        await using var _ = host;
        using var __ = manager;
        var (jobA, occA) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        var (jobB, occB) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        var (jobC, occC) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        var (jobD, occD) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        var (attemptA, tokenA) = await ClaimAsync(manager, jobA, occA, "a", ct);
        await ClaimAsync(manager, jobB, occB, "b", ct);
        await ClaimForOtherManagerAsync(db, jobC, occC, ct);

        await manager.RevokeAsync(ct);

        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE manager_id = '{manager.ManagerId}' AND state = 'Stale' AND close_reason = 'manager-revoked'", ct)).ShouldBe(2);
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE manager_id = '{manager.ManagerId}' AND state = 'Active'", ct)).ShouldBe(0);
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE id IN ('{jobA}', '{jobB}') AND state = 'Ready' AND attempts_used = 1 AND current_attempt_id IS NULL", ct)).ShouldBe(2);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences WHERE cause = 'attempt-stale'", ct)).ShouldBe(2);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE manager_id <> '" + manager.ManagerId + "' AND state = 'Active'", ct)).ShouldBe(1);
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE id = '{jobC}' AND state = 'Claimed'", ct)).ShouldBe(1);
        (await ScalarAsync(db,
            "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.revoked' AND details->>'fencedAttempts' = '2' AND details->>'revokedCredentials' = '0'", ct)).ShouldBe(1);

        using var claim = await manager.ClaimAsync(jobD, occD, "later", ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var heartbeat = $"/api/v1/analysis/attempts/{attemptA}/heartbeats";
        using var beat = await manager.SendAsync(HttpMethod.Post, heartbeat,
            new { logicalJobId = jobA, fencingToken = tokenA, observedAt = host.Time.GetUtcNow(), executionState = "running" }, null, ct);
        beat.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var completion = await manager.SendAsync(HttpMethod.Post, $"/api/v1/analysis/attempts/{attemptA}/completion", new { }, null, ct);
        completion.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var credentials = await manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/queue-credentials", new { }, null, ct);
        credentials.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FencingTheLastPermittedAttemptFailsTheJobPermanently()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host, manager) = await StartAsync(ct);
        await using var _ = host;
        using var __ = manager;
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        await ClaimAsync(manager, job, occ, "only", ct);
        await ExecAsync(db, $"UPDATE socalytics.analysis_logical_jobs SET attempts_used = 2 WHERE id = '{job}'", ct);

        await manager.RevokeAsync(ct);

        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE id = '{job}' AND state = 'Failed' AND failure_reason = 'attempts-exhausted'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_runs WHERE state = 'Running'", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task UnregistrationCompletionFencesAttemptsAndRevokesCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host, manager) = await StartAsync(ct);
        await using var _ = host;
        using var __ = manager;
        var registration = await RegistrationIdAsync(db, ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        await ClaimAsync(manager, job, occ, "k", ct);
        await ExecAsync(db,
            $"""
            INSERT INTO socalytics.analyst_manager_queue_credential (queue_credential_id, registration_id, user_public_key, issued_at, expires_at)
            VALUES (gen_random_uuid(), '{registration}', 'U{new string('A', 55)}', now(), now() + interval '1 hour'),
                   (gen_random_uuid(), '{registration}', 'U{new string('B', 55)}', now(), now() + interval '1 hour');
            """, ct);

        using var begin = await manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/unregistration", null, null, ct);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
        using var done = await manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/unregistration/completion", null, null, ct);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(ct));

        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE manager_id = '{manager.ManagerId}' AND state = 'Stale'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NOT NULL AND revocation_pushed_at IS NULL", ct)).ShouldBe(2);
        (await ScalarAsync(db,
            "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.unregistered' AND details->>'fencedAttempts' = '1' AND details->>'revokedCredentials' = '2'", ct)).ShouldBe(1);
    }
}
