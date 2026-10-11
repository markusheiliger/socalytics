using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class CompletionTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed record Seed(Guid Run, Guid Node, Guid Job, Guid Occurrence, Guid Team, Guid Match);

    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<Seed> SeedReadyJobAsync(IsolatedDatabase db, CancellationToken ct)
    {
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = DateTimeOffset.UtcNow;
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @team, @match, @g, 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES (@run, '{}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
            VALUES (@node, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
            VALUES (@job, @run, @node, 'match', 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, @d, @d, @d, '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @occ WHERE id = @job;
            """, c);
        cmd.Parameters.AddWithValue("run", s.Run);
        cmd.Parameters.AddWithValue("node", s.Node);
        cmd.Parameters.AddWithValue("job", s.Job);
        cmd.Parameters.AddWithValue("occ", s.Occurrence);
        cmd.Parameters.AddWithValue("team", s.Team);
        cmd.Parameters.AddWithValue("match", s.Match);
        cmd.Parameters.AddWithValue("d", Digest);
        cmd.Parameters.AddWithValue("g", Guid.NewGuid());
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct);
        return s;
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        await host.StartAsync(ct);
        return (db, host);
    }

    private static async Task<(Guid Attempt, long Token, int Number)> ClaimAsync(TestManagerClient m, Seed s, string key, CancellationToken ct)
    {
        using var r = await m.ClaimAsync(s.Job, s.Occurrence, key, ct: ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        return (b.GetProperty("attemptId").GetGuid(), b.GetProperty("fencingToken").GetInt64(), b.GetProperty("attemptNumber").GetInt32());
    }

    private static JsonObject Body(Seed s, Guid attempt, long token, int number, string key, bool succeeded)
    {
        var body = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["idempotencyKey"] = key,
            ["teamId"] = s.Team,
            ["matchId"] = s.Match,
            ["runId"] = s.Run,
            ["nodeId"] = s.Node,
            ["logicalJobId"] = s.Job,
            ["attemptId"] = attempt,
            ["attemptNumber"] = number,
            ["fencingToken"] = token,
            ["outcome"] = succeeded ? "succeeded" : "failed",
            ["capability"] = new JsonObject { ["id"] = "cap-a", ["version"] = "1.0.0" },
            ["schemaVersion"] = "1.0.0",
            ["executedRuntime"] = new JsonObject
            {
                ["runtimeProfile"] = "cpu", ["runtimeVersion"] = "1.0", ["operatingSystem"] = "linux",
                ["architecture"] = "x64", ["accelerators"] = new JsonArray(),
            },
            ["executedImageDigest"] = Digest,
            ["executedModelDigest"] = Digest,
            ["acceptedUpstreamResultIds"] = new JsonArray(),
            ["completedAt"] = "2026-01-01T00:00:00Z",
            ["correlation"] = new JsonObject { ["correlationId"] = Guid.NewGuid() },
        };
        if (succeeded)
        {
            body["result"] = new JsonObject { ["manifestReference"] = "results/m1.json", ["manifestChecksum"] = Digest };
        }
        else
        {
            body["failure"] = new JsonObject { ["category"] = "analyst-error" };
        }

        return body;
    }

    private static async Task<HttpResponseMessage> CompleteAsync(PlatformApiFactory factory, TestManagerClient m, Guid attempt, JsonObject body, CancellationToken ct)
    {
        using var client = factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, m.ManagerId.ToString());
        return await client.SendAsync(request, ct);
    }

    [Fact]
    public async Task MatchingCompletionRecordsOneAcceptedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);

        using var r = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "done-1", true), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        b.GetProperty("outcome").GetString().ShouldBe("accepted");
        b.GetProperty("replayed").GetBoolean().ShouldBeFalse();
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Completed'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Accepted'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task FailedOutcomeRetriesWhileBudgetRemains()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);

        using var r = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "fail-1", false), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("outcome").GetString().ShouldBe("failure-recorded");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences WHERE cause = 'attempt-failed'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task DigestMismatchIsRejectedAndRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "bad-1", true);
        body["executedImageDigest"] = "sha-256:" + new string('b', 64);

        using var r = await CompleteAsync(host.Factory, m, attempt, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.Content.ReadAsStringAsync(ct)).ShouldContain("completion-rejected");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Rejected'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task FencedAttemptAfterAcceptanceIsObsolete()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        using var ok = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "done-1", true), ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var late = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "done-2", true), ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await late.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("attemptNumber")]
    [InlineData("fencingToken")]
    [InlineData("idempotencyKey")]
    public async Task MissingRequiredFieldReturns400WithoutStateChange(string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "x-1", true);
        body.Remove(field);

        using var r = await CompleteAsync(host.Factory, m, attempt, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Active'", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe(0);
    }
}
