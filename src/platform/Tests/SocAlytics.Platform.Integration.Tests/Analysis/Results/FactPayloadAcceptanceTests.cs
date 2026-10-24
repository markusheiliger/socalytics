using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class FactPayloadAcceptanceTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private const string Sentinel = "PAYLOAD-VALUE-MUST-NOT-LEAK";
    private const string Schemas = """{"k":{"type":"object","required":["n"],"properties":{"n":{"type":"integer"}}}}""";
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    private sealed record Seed(Guid Run, Guid Node, Guid Job, Guid Occurrence, Guid Team, Guid Match);

    private static async Task<string> TextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? string.Empty;
    }

    private static async Task ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Seed> SeedAsync(IsolatedDatabase db, string? schemas, CancellationToken ct)
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
            VALUES (@node, @run, @snapshot::jsonb);
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
        cmd.Parameters.AddWithValue(
            "snapshot",
            schemas is null
                ? """{"analystProfileId":"profile","policyVersions":{}}"""
                : $$"""{"analystProfileId":"profile","policyVersions":{},"factPayloadSchemas":{{schemas}}}""");
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

    private static async Task<JsonElement> OutcomeAsync(HttpResponseMessage r, CancellationToken ct)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
    }

    private static List<FactSpec> Conforming(int count) =>
        Enumerable.Range(0, count).Select(i => new FactSpec("k", i * 1000L, i * 1000L + 500, new JsonObject { ["n"] = i, ["extra"] = "v" + i })).ToList();

    private static Task<string> TraceAsync(IsolatedDatabase db, CancellationToken ct) =>
        TextAsync(db,
            "SELECT (SELECT count(*) FROM socalytics.analysis_accepted_results) || '/' || (SELECT count(*) FROM socalytics.analysis_accepted_result_manifests) || '/' || " +
            "(SELECT count(*) FROM socalytics.analysis_indexed_facts)", ct);

    private async Task<string> RejectAsync(IReadOnlyList<FactSpec> facts, string expectedCode, Action<IServiceCollection>? configure = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct, configure);
        await using var _ = host;
        var s = await SeedAsync(db, Schemas, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: facts, ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "bad-1", prepared), ct);
        var outcome = await OutcomeAsync(r, ct);
        outcome.GetProperty("outcome").GetString().ShouldBe("failed");
        var reason = outcome.GetProperty("reason").GetString()!;
        reason.ShouldStartWith(expectedCode);
        reason.ShouldNotContain(Sentinel);

        (await TraceAsync(db, ct)).ShouldBe("0/0/0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe("1");
        (await TextAsync(db, "SELECT reason_code FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe(expectedCode);
        (await TextAsync(db, "SELECT string_agg(t::text, '') FROM socalytics.security_audit_event t", ct)).ShouldNotContain(Sentinel);
        (await TextAsync(db, "SELECT close_reason FROM socalytics.analysis_execution_attempts", ct)).ShouldNotContain(Sentinel);
        return reason;
    }

    [Fact]
    public async Task ConformingResultIsAcceptedAndPayloadsAreReadBackIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, Schemas, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var facts = Conforming(25);
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: facts, ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
        (await OutcomeAsync(r, ct)).GetProperty("outcome").GetString().ShouldBe("accepted");
        (await TraceAsync(db, ct)).ShouldBe("1/1/25");

        for (var i = 0; i < facts.Count; i++)
        {
            var stored = JsonNode.Parse(await TextAsync(db, $"SELECT payload::text FROM socalytics.analysis_indexed_facts WHERE ordinal = {i}", ct))!;
            JsonNode.DeepEquals(stored, facts[i].Payload).ShouldBeTrue($"payload {i}");
        }
    }

    [Fact]
    public async Task OneBadPayloadAmongManyFailsWithPayloadInvalidAndLeavesNoTrace()
    {
        var facts = Conforming(30);
        facts[17] = new FactSpec("k", 17_000, 17_500, new JsonObject { ["n"] = Sentinel });
        var reason = await RejectAsync(facts, ResultRejectionReasons.PayloadInvalid);
        reason.ShouldContain("k");
    }

    [Fact]
    public async Task UndeclaredKindFailsWithKindUndeclared()
    {
        var facts = Conforming(5);
        facts[3] = new FactSpec("other-kind", 3_000, 3_500, new JsonObject { ["n"] = 3, ["note"] = Sentinel });
        await RejectAsync(facts, ResultRejectionReasons.PayloadKindUndeclared);
    }

    [Fact]
    public async Task SizeLimitViolationKeepsItsExistingReason()
    {
        var facts = Conforming(3);
        facts[1] = new FactSpec("k", 1_000, 1_500, new JsonObject { ["n"] = Sentinel, ["pad"] = new string('x', 512) });
        await RejectAsync(facts, ResultRejectionReasons.FactsTooLarge, services => services.Configure<AnalysisResultOptions>(o => o.MaxFactPayloadBytes = 256));
    }

    [Theory]
    [InlineData("fenced")]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    public async Task FencedCancelledOrSupersededAttemptKeepsItsExistingOutcome(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, Schemas, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var facts = Conforming(3);
        facts[1] = new FactSpec("k", 1_000, 1_500, new JsonObject { ["n"] = Sentinel });
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: facts, ct: ct);

        if (kind != "fenced")
        {
            await ExecuteAsync(db,
                $"""
                UPDATE socalytics.analysis_execution_attempts SET state = 'Cancelled' WHERE id = '{attempt}';
                UPDATE socalytics.analysis_logical_jobs SET state = 'Cancelled' WHERE id = '{s.Job}';
                """, ct);
            if (kind == "superseded")
            {
                await ExecuteAsync(db,
                    $"UPDATE socalytics.analysis_runs SET state = 'Cancelled', terminated_at = now(), cancellation_cause = 'superseded' WHERE id = '{s.Run}'", ct);
            }
        }

        var before = await TextAsync(db, "SELECT string_agg(t::text, '|') FROM socalytics.analysis_execution_attempts t", ct);
        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, kind == "fenced" ? token + 1000 : token, number, "late-1", prepared), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict, await r.Content.ReadAsStringAsync(ct));
        (await r.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");
        (await TraceAsync(db, ct)).ShouldBe("0/0/0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe("0");
        (await TextAsync(db, "SELECT string_agg(t::text, '|') FROM socalytics.analysis_execution_attempts t", ct)).ShouldBe(before);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    public async Task CapabilityWithoutSchemasAndRunWithoutSnapshotSchemasBehaveAsBefore(string pinned)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, pinned == "none" ? null : "{}", ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var facts = new List<FactSpec>
        {
            new("k", 0, 500, new JsonObject { ["n"] = Sentinel }),
            new("anything", 1_000, 1_500, new JsonObject { ["free"] = new JsonArray(1, "two") }),
        };
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: facts, ct: ct);

        using var r = await CompleteAsync(host, m, attempt, Body(s, attempt, token, number, "done-1", prepared), ct);
        (await OutcomeAsync(r, ct)).GetProperty("outcome").GetString().ShouldBe("accepted");
        (await TraceAsync(db, ct)).ShouldBe("1/1/2");
        for (var i = 0; i < facts.Count; i++)
        {
            var stored = JsonNode.Parse(await TextAsync(db, $"SELECT payload::text FROM socalytics.analysis_indexed_facts WHERE ordinal = {i}", ct))!;
            JsonNode.DeepEquals(stored, facts[i].Payload).ShouldBeTrue($"payload {i}");
        }
    }

    [Fact]
    public async Task RetryRulesApplyToPayloadFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedAsync(db, Schemas, ct);
        using var m = new TestManagerClient(host.Factory);

        var (attempt, token, number) = await ClaimAsync(m, s, "c1", null, ct);
        var bad = new List<FactSpec> { new("k", 0, 500, new JsonObject { ["n"] = "not-an-integer" }) };
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: bad, ct: ct);
        var body = Body(s, attempt, token, number, "bad-1", prepared);
        using (var r = await CompleteAsync(host, m, attempt, body, ct))
        {
            var outcome = await OutcomeAsync(r, ct);
            outcome.GetProperty("outcome").GetString().ShouldBe("failed");
            outcome.GetProperty("reason").GetString()!.ShouldStartWith(ResultRejectionReasons.PayloadInvalid);
        }

        (await TextAsync(db, "SELECT state FROM socalytics.analysis_logical_jobs", ct)).ShouldBe("Ready");
        (await TextAsync(db, "SELECT count(*) FROM socalytics.analysis_readiness_occurrences WHERE cause = 'attempt-failed'", ct)).ShouldBe("1");

        // A replay returns the stored reason and neither audits nor changes anything.
        using (var replay = await CompleteAsync(host, m, attempt, body, ct))
        {
            var replayed = await OutcomeAsync(replay, ct);
            replayed.GetProperty("replayed").GetBoolean().ShouldBeTrue();
            replayed.GetProperty("reason").GetString()!.ShouldStartWith(ResultRejectionReasons.PayloadInvalid);
        }

        (await TextAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.result-rejected'", ct)).ShouldBe("1");

        // The retry within the attempt budget is accepted with conforming payloads.
        var occurrence = Guid.Parse(await TextAsync(db, "SELECT current_readiness_occurrence_id FROM socalytics.analysis_logical_jobs", ct));
        var (attempt2, token2, number2) = await ClaimAsync(m, s, "c2", occurrence, ct);
        var prepared2 = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt2, facts: Conforming(4), ct: ct);
        using (var ok = await CompleteAsync(host, m, attempt2, Body(s, attempt2, token2, number2, "done-2", prepared2), ct))
        {
            (await OutcomeAsync(ok, ct)).GetProperty("outcome").GetString().ShouldBe("accepted");
        }

        (await TextAsync(db, "SELECT state FROM socalytics.analysis_logical_jobs", ct)).ShouldBe("Completed");
        (await TraceAsync(db, ct)).ShouldBe("1/1/4");
    }
}
