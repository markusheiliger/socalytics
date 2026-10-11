using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SocAlytics.Platform.Application.Analysis.Commands;
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
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest)
            VALUES (@run, '{}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
            VALUES (@node, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
            VALUES (@job, @run, @node, 'match', 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{}'::jsonb, '1.0.0');
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
    public async Task HeartbeatRenewsOnlyMatchingCurrentAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        var (otherJob, _) = await SeedJobAsync(db, "Ready", ct);
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);

        using var claim = await m.ClaimAsync(job, occ, "k1", ct: ct);
        var a = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attempt = a.GetProperty("attemptId").GetGuid();
        var token = a.GetProperty("fencingToken").GetInt64();
        var before = a.GetProperty("leaseExpiresAt").GetDateTimeOffset();

        async Task<long> LeaseTicksAsync() => await ScalarAsync(db,
            "SELECT (extract(epoch FROM lease_expires_at) * 1000000)::bigint FROM socalytics.analysis_execution_attempts", ct);
        var unchanged = await LeaseTicksAsync();

        foreach (var (c, id, j, t) in new[]
        {
            (other, attempt, job, token),
            (m, attempt, job, token + 1),
            (m, attempt, otherJob, token),
        })
        {
            using var r = await c.HeartbeatAsync(id, j, t, ct);
            r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await r.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");
        }

        using var fabricated = await m.HeartbeatAsync(Guid.NewGuid(), job, token, ct);
        fabricated.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LeaseTicksAsync()).ShouldBe(unchanged);

        using var ok = await m.HeartbeatAsync(attempt, job, token, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var renewed = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(ct)).RootElement;
        renewed.GetProperty("leaseExpiresAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(before);

        host.Time.Advance(TimeSpan.FromMinutes(5));

        var expired = await LeaseTicksAsync();
        using var late = await m.HeartbeatAsync(attempt, job, token, ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LeaseTicksAsync()).ShouldBe(expired);

        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE socalytics.analysis_execution_attempts SET lease_expires_at = '2100-01-01'; " +
                "UPDATE socalytics.analysis_execution_attempts SET state = 'Stale', closed_at = now(), close_reason = 'lease-expired'; " +
                "UPDATE socalytics.analysis_logical_jobs SET current_attempt_id = NULL WHERE id = '" + job + "'", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var stale = await m.HeartbeatAsync(attempt, job, token, ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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

    private static async Task<int> ExpireAsync(AnalysisTestHost host, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct);
    }

    [Fact]
    public async Task ExpiredLeaseRequeuesJobAndFailsAfterLastAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        using var m = new TestManagerClient(host.Factory);

        using var first = await m.ClaimAsync(job, occ, "k1", ct: ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ExpireAsync(host, ct)).ShouldBe(0);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await ExpireAsync(host, ct)).ShouldBe(1);

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Stale' AND close_reason = 'lease-expired'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences WHERE cause = 'attempt-stale' AND sequence = 2", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready' AND current_attempt_id IS NULL", ct)).ShouldBe(1);

        var occ2 = Guid.Parse((await ScalarTextAsync(db, "SELECT current_readiness_occurrence_id::text FROM socalytics.analysis_logical_jobs", ct))!);
        using var second = await m.ClaimAsync(job, occ2, "k2", ct: ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await ExpireAsync(host, ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Failed' AND failure_reason = 'attempts-exhausted'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences", ct)).ShouldBe(2);
        (await ExpireAsync(host, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ExpiryWhileHostWasDisposedIsHandledAfterRecreation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Test, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        using (var m = new TestManagerClient(host.Factory))
        {
            using var claim = await m.ClaimAsync(job, occ, "k1", ct: ct);
            claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.RestartAsync(ct);
        (await ExpireAsync(host, ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task RevokedManagerIsRefusedAndAttemptRecoversThroughExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ManagerAuthentication.Real, ct);
        await using var _ = host;
        var (job, occ) = await SeedJobAsync(db, "Ready", ct);
        using var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);
        using var claim = await manager.ClaimAsync(job, occ, "real-1", ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var path = $"/api/v1/analysis/attempts/{a.GetProperty("attemptId").GetGuid()}/heartbeats";
        var body = new { logicalJobId = job, fencingToken = a.GetProperty("fencingToken").GetInt64(), observedAt = host.Time.GetUtcNow(), executionState = "running" };

        using var ok = await manager.SendAsync(HttpMethod.Post, path, body, null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        await manager.RevokeAsync(ct);
        using var refused = await manager.SendAsync(HttpMethod.Post, path, body, null, ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await ExpireAsync(host, ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready'", ct)).ShouldBe(1);
    }

    private static async Task<string?> ScalarTextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (await cmd.ExecuteScalarAsync(ct))?.ToString();
    }
}
