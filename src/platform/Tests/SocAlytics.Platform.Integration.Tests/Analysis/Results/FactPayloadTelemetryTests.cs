using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

// The meter SocAlytics.Platform.Analysis is process-wide and this class listens to it without scope while test collections run in
// parallel in one process, so a foreign rejection could break its counts. The collection runs it apart from parallel collections.
[Collection(AnalysisTelemetryCollection.Name)]
public sealed class FactPayloadTelemetryTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private const string Sentinel = "PAYLOAD-VALUE-MUST-NOT-LEAK";
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
            """
            {"analystProfileId":"profile","policyVersions":{},"factPayloadSchemas":{"k":{"type":"object","required":["n"],"properties":{"n":{"type":"integer"}}}}}
            """);
        await cmd.ExecuteNonQueryAsync(ct);
        return s;
    }

    private static JsonObject Body(Seed s, Guid attempt, long token, int number, PreparedResult result) => new()
    {
        ["contractVersion"] = "1.0.0",
        ["idempotencyKey"] = "payload-telemetry",
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

    [Theory]
    [InlineData("invalid", "result-payload-invalid")]
    [InlineData("undeclared", "result-payload-kind-undeclared")]
    public async Task MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue(string defect, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        var measurements = new List<(string? Outcome, string Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == AnalysisMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, _, t, _) =>
        {
            var tags = t.ToArray();
            var outcome = tags.FirstOrDefault(x => x.Key == "outcome").Value as string;
            var text = i.Name + ":" + string.Join(";", tags.Select(x => $"{x.Key}={x.Value}"));
            lock (measurements)
            {
                measurements.Add((i.Name == "socalytics.analysis.completions_rejected" ? outcome : null, text));
            }
        });
        listener.Start();

        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        await host.StartAsync(ct);
        var s = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);

        using var claim = await m.ClaimAsync(s.Job, s.Occurrence, "c1", ct: ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
        var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attempt = claimed.GetProperty("attemptId").GetGuid();

        var kind = defect == "invalid" ? "k" : "other-kind";
        var payload = defect == "invalid" ? new JsonObject { ["n"] = Sentinel } : new JsonObject { ["n"] = 1, ["note"] = Sentinel };
        var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, facts: [new FactSpec(kind, 100, 200, payload)], ct: ct);
        var body = Body(s, attempt, claimed.GetProperty("fencingToken").GetInt64(), claimed.GetProperty("attemptNumber").GetInt32(), prepared);

        using var client = host.Factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion") { Content = JsonContent.Create(body) };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, m.ManagerId.ToString());
        using var response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var outcome = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        outcome.GetProperty("outcome").GetString().ShouldBe("failed");
        outcome.GetProperty("reason").GetString()!.ShouldStartWith(code);

        // The metric tag is the bare reason code, distinct from other acceptance failures.
        lock (measurements)
        {
            measurements.Count(x => x.Outcome == code).ShouldBe(1);
            measurements.Count(x => x.Outcome is not null && x.Outcome != code).ShouldBe(0);
            measurements.ShouldAllBe(x => !x.Tags.Contains(Sentinel, StringComparison.Ordinal));
        }

        // The audit record carries the bare code and no payload content.
        const string eventFilter = "WHERE event_type = 'analysis.result-rejected'";
        (await TextAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event {eventFilter}", ct)).ShouldBe("1");
        (await TextAsync(db, $"SELECT reason_code FROM socalytics.security_audit_event {eventFilter}", ct)).ShouldBe(code);
        (await TextAsync(db, $"SELECT t::text FROM socalytics.security_audit_event t {eventFilter}", ct)).ShouldNotContain(Sentinel);
        (await TextAsync(db, "SELECT close_reason FROM socalytics.analysis_execution_attempts", ct)).ShouldNotContain(Sentinel);
    }
}
