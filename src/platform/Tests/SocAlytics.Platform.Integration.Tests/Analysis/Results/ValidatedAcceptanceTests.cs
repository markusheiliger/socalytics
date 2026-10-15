using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class ValidatedAcceptanceTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    private sealed record Seed(Guid Run, Guid Node, Guid Job, Guid Occurrence, Guid Team, Guid Match);

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
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @team, @match, @g, 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', now(), now(), now());
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

    private static async Task<string> StateAsync(IsolatedDatabase db, CancellationToken ct) =>
        await TextAsync(db,
            "SELECT (SELECT string_agg(state, ',') FROM socalytics.analysis_execution_attempts) || '/' || (SELECT string_agg(state, ',') FROM socalytics.analysis_logical_jobs) || '/' || " +
            "(SELECT count(*) FROM socalytics.analysis_accepted_results) || '/' || (SELECT count(*) FROM socalytics.analysis_accepted_result_manifests) || '/' || (SELECT count(*) FROM socalytics.analysis_completion_outcomes)", ct);

    [Fact]
    public async Task ValidManifestIsAcceptedAndRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("outcome").GetString().ShouldBe("accepted");
        (await StateAsync(db, ct)).ShouldBe("Accepted/Completed/1/1/1");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe("0");
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("scope")]
    public async Task InvalidManifestIsRejectedAuditedAndRetried(string defect)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(
            db, rustFs, attempt,
            mutate: defect == "scope" ? manifest => manifest["scope"]!["matchId"] = Guid.NewGuid().ToString() : null,
            uploadManifest: defect != "missing", ct: ct);
        var body = Body(s, attempt, token, number, "bad-1", prepared);
        if (defect == "checksum")
        {
            body["result"]!["manifestChecksum"] = "sha-256:" + new string('9', 64);
        }

        var expected = defect switch
        {
            "checksum" => "result-manifest-checksum-mismatch",
            "missing" => "result-manifest-missing",
            _ => "result-scope-mismatch",
        };

        using var r = await CompleteAsync(host, m, attempt, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var first = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        first.GetProperty("outcome").GetString().ShouldBe("failed");
        first.GetProperty("reason").GetString().ShouldBe(expected);
        (await StateAsync(db, ct)).ShouldBe("Failed/Ready/0/0/1");
        (await TextAsync(db, "SELECT close_reason FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(expected);
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences WHERE cause = 'attempt-failed'", ct)).ShouldBe("1");

        var auditSql = "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'";
        (await TextAsync(db, auditSql, ct)).ShouldBe("1");
        (await TextAsync(db, "SELECT reason_code FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe(expected);
        (await TextAsync(db, "SELECT details::text FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct))
            .ShouldNotContain("results/");

        // A replay returns the stored reason without verifying or auditing again.
        var before = await StateAsync(db, ct);
        using var replay = await CompleteAsync(host, m, attempt, body, ct);
        var replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(ct)).RootElement;
        replayed.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        replayed.GetProperty("reason").GetString().ShouldBe(expected);
        (await StateAsync(db, ct)).ShouldBe(before);
        (await TextAsync(db, auditSql, ct)).ShouldBe("1");

        // The retry within the attempt budget can be accepted.
        var occurrence = Guid.Parse(await TextAsync(db, "SELECT current_readiness_occurrence_id FROM socalytics.analysis_logical_jobs", ct));
        var (attempt2, token2, number2) = await ClaimAsync(m, s, "c2", occurrence, ct);
        var prepared2 = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt2, ct: ct);
        using var ok = await CompleteAsync(host, m, attempt2, Body(s, attempt2, token2, number2, "done-2", prepared2), ct);
        JsonDocument.Parse(await ok.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("outcome").GetString().ShouldBe("accepted");
        (await StateAsync(db, ct)).ShouldBe("Failed,Accepted/Completed/1/1/2");
    }

    [Fact]
    public async Task TenConcurrentIdenticalCompletionsVerifyAndAcceptOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, ct: ct);
        var body = Body(s, attempt, token, number, "done-1", prepared);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var r = await CompleteAsync(host, m, attempt, (JsonObject)JsonNode.Parse(body.ToJsonString())!, ct);
            return (r.StatusCode, Text: await r.Content.ReadAsStringAsync(ct));
        }));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK, string.Join("\n", responses.Select(r => r.Text)));
        responses.Count(r => !JsonDocument.Parse(r.Text).RootElement.GetProperty("replayed").GetBoolean()).ShouldBe(1);
        (await StateAsync(db, ct)).ShouldBe("Accepted/Completed/1/1/1");
    }

    [Fact]
    public async Task UnavailableStorageReturns503WithoutStateChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct, services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton(rustFs.CreateObjectStorage("http://127.0.0.1:9"));
        });
        await using var _ = host;
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, ct: ct);
        var before = await StateAsync(db, ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await r.Content.ReadAsStringAsync(ct));
        (await r.Content.ReadAsStringAsync(ct)).ShouldContain("object-storage-unavailable");
        (await StateAsync(db, ct)).ShouldBe(before);
        (await TextAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe("0");
    }
}
