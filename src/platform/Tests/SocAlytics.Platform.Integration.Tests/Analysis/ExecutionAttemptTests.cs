using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class ExecutionAttemptTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<(Guid Job, Guid Occurrence)> SeedJobAsync(IsolatedDatabase db, string state, CancellationToken ct)
    {
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), occ = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @g, @g, @g, 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
            VALUES (@job, @run, @node, 'match', 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, 'sha-256:a', 'sha-256:a', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = @state, current_readiness_occurrence_id = @occ WHERE id = @job;
            """, c);
        cmd.Parameters.AddWithValue("run", run);
        cmd.Parameters.AddWithValue("node", node);
        cmd.Parameters.AddWithValue("job", job);
        cmd.Parameters.AddWithValue("occ", occ);
        cmd.Parameters.AddWithValue("g", Guid.NewGuid());
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("state", state);
        await cmd.ExecuteNonQueryAsync(ct);
        return (job, occ);
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(ManagerAuthentication auth, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: auth);
        await host.StartAsync(ct);
        return (db, host);
    }

    [Fact]
    public async Task TenSimultaneousClaimantsCreateExactlyOneAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        var clients = Enumerable.Range(0, 10).Select(_ => new TestManagerClient(host.Factory)).ToList();
        var responses = await Task.WhenAll(clients.Select(async c =>
        {
            using var r = await c.ClaimAsync(job, occ, "key-" + c.ManagerId, ct: ct);
            return (r.StatusCode, Body: await r.Content.ReadAsStringAsync(ct));
        }));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict && r.Body.Contains("claim-obsolete")).ShouldBe(9);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT attempt_number FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT attempts_used FROM socalytics.analysis_logical_jobs", ct)).ShouldBe(1);
        clients.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task ReplayReusedKeyAndObsoleteOutcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        var (done, doneOcc) = await SeedJobAsync(db, "Completed", ct);
        var (blocked, blockedOcc) = await SeedJobAsync(db, "Blocked", ct);
        using var m = new TestManagerClient(host.Factory);

        using var first = await m.ClaimAsync(job, occ, "k1", ct: ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Location!.ToString().ShouldStartWith("/api/v1/analysis/attempts/");
        var a = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct)).RootElement;
        a.GetProperty("replayed").GetBoolean().ShouldBeFalse();
        a.GetProperty("leaseSeconds").GetInt32().ShouldBe(30);
        a.GetProperty("staleTimeoutSeconds").GetInt32().ShouldBe(90);

        using var replay = await m.ClaimAsync(job, occ, "k1", ct: ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var b = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(ct)).RootElement;
        b.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        b.GetProperty("fencingToken").GetInt64().ShouldBe(a.GetProperty("fencingToken").GetInt64());
        b.GetProperty("leaseExpiresAt").GetDateTimeOffset().ShouldBe(a.GetProperty("leaseExpiresAt").GetDateTimeOffset());

        using var reused = await m.ClaimAsync(job, Guid.NewGuid(), "k1", ct: ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reused.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-reused");

        using var superseded = await m.ClaimAsync(job, Guid.NewGuid(), "k2", ct: ct);
        superseded.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await superseded.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");
        using var c1 = await m.ClaimAsync(done, doneOcc, "k3", ct: ct);
        using var c2 = await m.ClaimAsync(blocked, blockedOcc, "k4", ct: ct);
        (await c1.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");
        (await c2.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");
        using var unknown = await m.ClaimAsync(Guid.NewGuid(), occ, "k5", ct: ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT max(attempt_number) FROM socalytics.analysis_execution_attempts", ct)).ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task MissingKeyAndPrincipalsAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        using var m = new TestManagerClient(host.Factory);

        using var missing = await m.ClaimAsync(job, occ, null, ct: ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-missing");
        using var wrongScope = await m.ClaimAsync(job, occ, "k1", noScope: true, ct: ct);
        wrongScope.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var anon = host.Factory.CreateApiClient();
        using var unauth = await anon.PostAsync($"/api/v1/analysis/logical-jobs/{job}/claims", null, ct);
        unauth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisteredManagerClaimsThroughRealScheme()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Real, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        using var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);

        using var response = await manager.ClaimAsync(job, occ, "real-1", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT manager_id FROM socalytics.analysis_execution_attempts", c);
        ((Guid)(await cmd.ExecuteScalarAsync(ct))!).ShouldBe(manager.ManagerId);
    }
}
