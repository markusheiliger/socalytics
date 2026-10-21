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

public sealed class ClaimIdempotencyTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<(Guid Job, Guid Occurrence)> SeedReadyJobAsync(IsolatedDatabase db, CancellationToken ct)
    {
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), occ = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @g, @g, @g, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
            VALUES (@job, @run, @node, 'match', 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, resources, schema_version)
            VALUES (@job, 3, 30, 90, 600, 'sha-256:a', 'sha-256:a', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @occ WHERE id = @job;
            """, c);
        cmd.Parameters.AddWithValue("run", run);
        cmd.Parameters.AddWithValue("node", node);
        cmd.Parameters.AddWithValue("job", job);
        cmd.Parameters.AddWithValue("occ", occ);
        cmd.Parameters.AddWithValue("g", Guid.NewGuid());
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct);
        return (job, occ);
    }

    private static async Task<Guid> MarkAttemptStaleAsync(IsolatedDatabase db, Guid job, CancellationToken ct)
    {
        var occ = Guid.NewGuid();
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE socalytics.analysis_execution_attempts SET state = 'Stale', closed_at = now() WHERE logical_job_id = @job;
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            VALUES (@occ, @job, 2, 'attempt-stale', now());
            UPDATE socalytics.analysis_logical_jobs
            SET state = 'Ready', current_attempt_id = NULL, current_readiness_occurrence_id = @occ WHERE id = @job;
            """, c);
        cmd.Parameters.AddWithValue("job", job);
        cmd.Parameters.AddWithValue("occ", occ);
        await cmd.ExecuteNonQueryAsync(ct);
        return occ;
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        await host.StartAsync(ct);
        return (db, host);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r, CancellationToken ct) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    [Fact]
    public async Task RetryWithSameKeyReplaysTheOriginalGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var (job, occ) = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);

        using var first = await m.ClaimAsync(job, occ, "k1", ct: ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = await BodyAsync(first, ct);

        using var retry = await m.ClaimAsync(job, occ, "k1", ct: ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var b = await BodyAsync(retry, ct);
        b.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        b.GetProperty("attemptId").GetGuid().ShouldBe(a.GetProperty("attemptId").GetGuid());
        b.GetProperty("fencingToken").GetInt64().ShouldBe(a.GetProperty("fencingToken").GetInt64());
        b.GetProperty("leaseExpiresAt").GetDateTimeOffset().ShouldBe(a.GetProperty("leaseExpiresAt").GetDateTimeOffset());

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT attempts_used FROM socalytics.analysis_logical_jobs", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task TenConcurrentRetriesWithOneKeyCreateOneAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var (job, occ) = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var r = await m.ClaimAsync(job, occ, "same", ct: ct);
            return (r.StatusCode, Body: await BodyAsync(r, ct));
        }));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(9);
        responses.Select(r => r.Body.GetProperty("attemptId").GetGuid()).Distinct().Count().ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT attempts_used FROM socalytics.analysis_logical_jobs", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task KeyReusedForAnotherJobOrOccurrenceConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var (job, occ) = await SeedReadyJobAsync(db, ct);
        var (other, otherOcc) = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        (await m.ClaimAsync(job, occ, "k1", ct: ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var otherJob = await m.ClaimAsync(other, otherOcc, "k1", ct: ct);
        otherJob.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await otherJob.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-reused");

        using var otherOccurrence = await m.ClaimAsync(job, Guid.NewGuid(), "k1", ct: ct);
        otherOccurrence.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await otherOccurrence.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-reused");

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task SameKeyAfterAttemptIsNoLongerCurrentIsObsolete()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var (job, occ) = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        (await m.ClaimAsync(job, occ, "k1", ct: ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var newOcc = await MarkAttemptStaleAsync(db, job, ct);

        using var sameOccurrence = await m.ClaimAsync(job, occ, "k1", ct: ct);
        sameOccurrence.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameOccurrence.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");

        using var newOccurrence = await m.ClaimAsync(job, newOcc, "k1", ct: ct);
        newOccurrence.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await newOccurrence.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-reused");

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT attempts_used FROM socalytics.analysis_logical_jobs", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task AnotherManagerPresentingTheSameKeyIsObsolete()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var (job, occ) = await SeedReadyJobAsync(db, ct);
        using var first = new TestManagerClient(host.Factory);
        using var second = new TestManagerClient(host.Factory);
        (await first.ClaimAsync(job, occ, "k1", ct: ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await second.ClaimAsync(job, occ, "k1", ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE manager_id = '" + second.ManagerId + "'", ct)).ShouldBe(0);
    }
}
