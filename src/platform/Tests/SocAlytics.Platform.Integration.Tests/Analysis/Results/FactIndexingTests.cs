using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class FactIndexingTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    private static readonly FactSpec[] Facts = [new("pass", 0, 1000), new("pass", 1000, 2500), new("shot", 5000, 5400000)];

    private sealed record Seed(Guid Run, Guid Node, Guid Job, Guid Occurrence, Guid Team, Guid Match);

    private sealed class ThrowingFactStore : IIndexedFactStore
    {
        public Task InsertAsync(IReadOnlyList<IndexedFactRecord> facts, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The unit of work fails after verification.");
    }

    private static async Task<string> TextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? string.Empty;
    }

    private static async Task<Seed> SeedAsync(IsolatedDatabase db, CancellationToken ct)
    {
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @team, @match, @g, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', now(), now(), now());
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', now());
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES (@run, '{}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
            VALUES (@node, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
            VALUES (@job, @run, @node, 'match', 'Waiting', now());
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, @d, @d, @d, '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            VALUES (@occ, @job, 1, 'initial', now());
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
        await cmd.ExecuteNonQueryAsync(ct);
        return s;
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct, Action<IServiceCollection>? configure = null)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, configureServices: configure, managerAuthentication: ManagerAuthentication.Test);
        await host.StartAsync(ct);
        return (db, host);
    }

    private static async Task<(Guid Attempt, long Token, int Number)> ClaimAsync(TestManagerClient m, Seed s, string key, Guid? occurrence, CancellationToken ct)
    {
        using var r = await m.ClaimAsync(s.Job, occurrence ?? s.Occurrence, key, ct: ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync(ct));
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        return (b.GetProperty("attemptId").GetGuid(), b.GetProperty("fencingToken").GetInt64(), b.GetProperty("attemptNumber").GetInt32());
    }

    private static JsonObject Body(Seed s, Guid attempt, long token, int number, string key, PreparedResult result) => new()
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
        ["outcome"] = "succeeded",
        ["result"] = new JsonObject { ["manifestReference"] = result.ManifestReference, ["manifestChecksum"] = result.ManifestChecksum },
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

    private static async Task<HttpResponseMessage> CompleteAsync(AnalysisTestHost host, TestManagerClient m, Guid attempt, JsonObject body, CancellationToken ct)
    {
        using var client = host.Factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion") { Content = JsonContent.Create(body) };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, m.ManagerId.ToString());
        return await client.SendAsync(request, ct);
    }

    private static Task<string> FactCountAsync(IsolatedDatabase db, CancellationToken ct) =>
        TextAsync(db, "SELECT count(*) FROM socalytics.analysis_indexed_facts", ct);

    [Fact]
    public async Task AcceptedFactsAreStoredOnceWithTheResultIdentityAndReplayAddsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: Facts, ct: ct);
        var body = Body(s, attempt, token, number, "done-1", prepared);

        using var r = await CompleteAsync(host, m, attempt, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var accepted = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        accepted.GetProperty("outcome").GetString().ShouldBe("accepted");
        var acceptedId = accepted.GetProperty("acceptedResultId").GetGuid();

        (await FactCountAsync(db, ct)).ShouldBe("3");
        (await TextAsync(db,
            $"""
            SELECT count(*) FROM socalytics.analysis_indexed_facts
            WHERE accepted_result_id = '{acceptedId}' AND run_id = '{s.Run}' AND node_id = '{s.Node}' AND team_id = '{s.Team}'
              AND match_id = '{s.Match}' AND capability_id = 'cap-a' AND capability_version = '1.0.0' AND schema_version = '1.0.0'
              AND output_type = 'foundational-facts'
            """, ct)).ShouldBe("3");
        (await TextAsync(db, "SELECT string_agg(ordinal || ':' || kind || ':' || start_ms || '-' || end_ms, ',' ORDER BY ordinal) FROM socalytics.analysis_indexed_facts", ct))
            .ShouldBe("0:pass:0-1000,1:pass:1000-2500,2:shot:5000-5400000");

        using var replay = await CompleteAsync(host, m, attempt, body, ct);
        JsonDocument.Parse(await replay.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        (await FactCountAsync(db, ct)).ShouldBe("3");
    }

    [Fact]
    public async Task ResultAcceptedWithoutFactArtifactsStoresNoFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("outcome").GetString().ShouldBe("accepted");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe("1");
        (await FactCountAsync(db, ct)).ShouldBe("0");
    }

    [Theory]
    [InlineData("out-of-window", "result-facts-out-of-window")]
    [InlineData("count-mismatch", "result-facts-count-mismatch")]
    [InlineData("invalid-range", "result-facts-invalid")]
    [InlineData("artifact-digest", "result-artifact-invalid")]
    public async Task RejectedResultsStoreNoFacts(string defect, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var facts = defect switch
        {
            "out-of-window" => [new FactSpec("pass", 5399000, 5400001)],
            "invalid-range" => [new FactSpec("pass", 100, 100)],
            _ => Facts,
        };
        var prepared = await ResultObjectsHelper.PrepareAsync(
            db, rustFs, attempt, facts: facts,
            mutate: manifest =>
            {
                if (defect == "count-mismatch")
                {
                    manifest["factCount"] = 7;
                }

                if (defect == "artifact-digest")
                {
                    manifest["artifacts"]![0]!["digest"] = "sha-256:" + new string('9', 64);
                }
            },
            ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "bad-1", prepared), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var outcome = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        outcome.GetProperty("outcome").GetString().ShouldBe("failed");
        outcome.GetProperty("reason").GetString().ShouldBe(reason);
        (await FactCountAsync(db, ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe("0");
    }

    [Fact]
    public async Task FailureAfterVerificationLeavesNoFactsAndNoAcceptedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct, services =>
        {
            services.RemoveAll<IIndexedFactStore>();
            services.AddScoped<IIndexedFactStore, ThrowingFactStore>();
        });
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: Facts, ct: ct);

        try
        {
            using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
            r.IsSuccessStatusCode.ShouldBeFalse();
        }
        catch (InvalidOperationException)
        {
        }

        (await FactCountAsync(db, ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT string_agg(state, ',') FROM socalytics.analysis_execution_attempts", ct)).ShouldBe("Active");
    }
}
