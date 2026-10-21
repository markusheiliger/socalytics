using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class UpstreamReadTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static readonly HttpClient Http = new();
    private const long SegmentMs = 300_000;

    private sealed record Seeded(Guid RunId, Guid RecordingId, Guid HighJob, Guid HighOccurrence, Guid[] SnapshotResults);

    private sealed record Claimed(Guid Attempt, long Token, DateTimeOffset LeaseExpiresAt);

    private static async Task ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        host.Time.SetUtcNow(DateTimeOffset.UtcNow);
        await host.StartAsync(ct);
        return (db, host);
    }

    // Segment node "low" with segments 1 and 2 accepted before the snapshot and 3 failed, and a match node "high" that depends on it.
    private async Task<Seeded> SeedAsync(IsolatedDatabase db, CancellationToken ct, bool partial = false)
    {
        Guid run = Guid.NewGuid(), low = Guid.NewGuid(), high = Guid.NewGuid(), recording = Guid.NewGuid(), highJob = Guid.NewGuid(), highOcc = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await ExecAsync(c,
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @g, @g, @g, now(), 'wf', '1.0.0', ARRAY['cap-high'], 'all-requested-capabilities', 'Running', @at, @at, @at);
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@low, @run, 'low', 'cap-low', '1.0.0', 'low-level', 'segment', 0, 'Active', @at),
                   (@high, @run, 'high', 'cap-high', '1.0.0', 'high-level', 'match', 1, 'Active', @at);
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest)
            VALUES (@run, '{"segmentationPolicy":{"durationSeconds":300}}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
            VALUES (@low, @run, '{"analystProfileId":"profile","policyVersions":{},"schemaVersion":"1.0.0"}'::jsonb),
                   (@high, @run, '{"analystProfileId":"profile","policyVersions":{},"schemaVersion":"1.0.0"}'::jsonb);
            INSERT INTO socalytics.analysis_dependency_edges (run_id, upstream_node_id, dependent_node_id, kind)
            VALUES (@run, @low, @high, 'required');
            """, ct, ("run", run), ("low", low), ("high", high), ("g", Guid.NewGuid()), ("at", at));

        var results = new List<Guid>();
        for (var n = 1; n <= 3; n++)
        {
            var job = Guid.NewGuid();
            var occ = Guid.NewGuid();
            var attempt = Guid.NewGuid();
            var state = n == 3 ? "Failed" : "Completed";
            await ExecAsync(c,
                """
                INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number, state, updated_at)
                VALUES (@job, @run, @low, 'segment', @rec, 'sha-256:a', 'sha-256:b', @n, @state, @at);
                INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
                VALUES (@job, 2, 30, 90, 600, 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{}'::jsonb, '1.0.0');
                INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occ, @job, 1, 'initial', @at);
                INSERT INTO socalytics.analysis_execution_attempts (id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
                VALUES (@attempt, @job, @occ, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Accepted', @at, @at, @at);
                """, ct, ("job", job), ("run", run), ("low", low), ("rec", recording), ("n", n), ("state", state), ("at", at), ("occ", occ), ("attempt", attempt));
            if (n == 3)
            {
                continue;
            }

            var prepared = await ResultObjectsHelper.PrepareAsync(db, rustFs, attempt, ct: ct);
            var id = Guid.NewGuid();
            results.Add(id);
            await ExecAsync(c,
                """
                INSERT INTO socalytics.analysis_accepted_results (id, logical_job_id, run_id, node_id, attempt_id, manifest_reference, manifest_checksum, capability_id, capability_version, schema_version, analyst_profile_digest, image_digest, model_digest, policy_versions, executed_runtime, upstream_result_ids, accepted_at)
                VALUES (@id, @job, @run, @low, @attempt, @ref, @sum, 'cap-low', '1.0.0', '1.0.0', 'sha-256:a', 'sha-256:a', NULL, '{}'::jsonb, '{}'::jsonb, '{}', @at);
                INSERT INTO socalytics.analysis_accepted_result_manifests (accepted_result_id, contract_version, output_type, validity_start_ms, validity_end_ms, fact_count, artifacts, upstream_lineage)
                VALUES (@id, '1.0.0', 'foundational-facts', @start, @end, 0, @artifacts::jsonb, '[]'::jsonb);
                """, ct, ("id", id), ("job", job), ("run", run), ("low", low), ("attempt", attempt), ("ref", prepared.ManifestReference),
                ("sum", prepared.ManifestChecksum), ("at", at), ("start", (n - 1) * SegmentMs), ("end", n * SegmentMs),
                ("artifacts", prepared.Manifest["artifacts"]!.ToJsonString()));
        }

        var inputs = partial
            ? JsonSerializer.Serialize(new[]
            {
                new
                {
                    upstreamNodeKey = "low", edgeKind = "required", status = "partial", acceptedResultIds = results,
                    coverageMode = "partial", minimumAcceptedFraction = 0.5m,
                    coverage = Enumerable.Range(1, 3).Select(n => new { segmentId = $"{recording}-{n}", state = n == 3 ? "failed" : "accepted" }).ToArray(),
                },
            })
            : JsonSerializer.Serialize(new[]
            {
                new { upstreamNodeKey = "low", edgeKind = "required", status = "accepted", acceptedResultIds = results },
            });
        await ExecAsync(c,
            """
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at) VALUES (@job, @run, @high, 'match', 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at) VALUES (@job, @inputs::jsonb, @at);
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @occ WHERE id = @job;
            """, ct, ("job", highJob), ("run", run), ("high", high), ("at", at), ("inputs", inputs), ("occ", highOcc));
        return new Seeded(run, recording, highJob, highOcc, [.. results]);
    }

    private static async Task<Claimed> ClaimAsync(Seeded seed, TestManagerClient manager, CancellationToken ct)
    {
        using var response = await manager.ClaimAsync(seed.HighJob, seed.HighOccurrence, "key-" + seed.HighJob, ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return new Claimed(a.GetProperty("attemptId").GetGuid(), a.GetProperty("fencingToken").GetInt64(), a.GetProperty("leaseExpiresAt").GetDateTimeOffset());
    }

    private static string Query(Claimed c, string extra = "", string capability = "cap-low", long? token = null) =>
        $"fencingToken={token ?? c.Token}&capabilityId={capability}&capabilityVersion=1.0.0&schemaVersion=1.0.0{extra}";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BodyAsync(HttpResponseMessage response, CancellationToken ct) =>
        (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement);

    private static async Task<string?> TextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (await cmd.ExecuteScalarAsync(ct))?.ToString();
    }

    [Fact]
    public async Task ReturnsSnapshotResultsCoverageAndReadableLeaseBoundedGrants()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var (status, body) = await BodyAsync(response, ct);
        status.ShouldBe(HttpStatusCode.OK, body.ToString());

        var validation = host.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.UpstreamResultsV1, body);
        validation.IsValid.ShouldBeTrue(string.Join("; ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));

        var results = body.GetProperty("results").EnumerateArray().ToList();
        results.Select(r => r.GetProperty("acceptedResultId").GetGuid()).Order().ShouldBe(seed.SnapshotResults.Order());
        results.Select(r => r.GetProperty("scope").GetProperty("segment").GetProperty("windowStartMs").GetInt64()).ShouldBe([0L, SegmentMs]);
        body.GetProperty("coverage").EnumerateArray().Select(e => e.GetProperty("state").GetString()).ShouldBe(["accepted", "accepted", "failed"]);
        body.GetProperty("continuationToken").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("upstream").GetProperty("edgeKind").GetString().ShouldBe("required");

        foreach (var r in results)
        {
            var grants = r.GetProperty("grants").EnumerateArray().ToList();
            grants.Select(g => g.GetProperty("name").GetString()).ShouldBe(["manifest.json", "facts.json"]);
            foreach (var g in grants)
            {
                g.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(c.LeaseExpiresAt.AddSeconds(1));
            }

            using var manifest = await Http.GetAsync(grants[0].GetProperty("url").GetString(), ct);
            manifest.StatusCode.ShouldBe(HttpStatusCode.OK);
            ResultObjectsHelper.DigestOf(await manifest.Content.ReadAsByteArrayAsync(ct)).ShouldBe(r.GetProperty("manifestChecksum").GetString());
            using var artifact = await Http.GetAsync(grants[1].GetProperty("url").GetString(), ct);
            artifact.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task CompleteDependencyReportsCompleteModeWithoutFractionAndKeepsStateMappedCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        var (status, body) = await BodyAsync(response, ct);
        status.ShouldBe(HttpStatusCode.OK, body.ToString());
        body.GetProperty("contractVersion").GetString().ShouldBe("1.1.0");
        var coverage = body.GetProperty("upstream").GetProperty("coverage");
        coverage.GetProperty("mode").GetString().ShouldBe("complete");
        coverage.TryGetProperty("minimumAcceptedFraction", out var fraction).ShouldBeFalse(fraction.ToString());
        body.GetProperty("coverage").EnumerateArray().Select(e => e.GetProperty("state").GetString()).ShouldBe(["accepted", "accepted", "failed"]);
    }

    [Fact]
    public async Task PartialDependencyServesSnapshotFixedCoverageOnEveryPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct, partial: true);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        // Later job states must not change the coverage the snapshot fixed.
        await using (var update = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await update.OpenAsync(ct);
            await ExecAsync(update, "UPDATE socalytics.analysis_logical_jobs SET state = 'Failed' WHERE segment_number = 1", ct);
        }

        string? token = null;
        var pages = 0;
        do
        {
            using var response = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, "&pageSize=1" + (token is null ? "" : "&continuationToken=" + token)), ct);
            var (status, body) = await BodyAsync(response, ct);
            status.ShouldBe(HttpStatusCode.OK, body.ToString());
            var validation = host.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.UpstreamResultsV1, body);
            validation.IsValid.ShouldBeTrue(string.Join("; ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));
            body.GetProperty("contractVersion").GetString().ShouldBe("1.1.0");
            var coverage = body.GetProperty("upstream").GetProperty("coverage");
            coverage.GetProperty("mode").GetString().ShouldBe("partial");
            coverage.GetProperty("minimumAcceptedFraction").GetDecimal().ShouldBe(0.5m);
            body.GetProperty("coverage").EnumerateArray().Select(e => (e.GetProperty("segmentId").GetString()!, e.GetProperty("state").GetString()!))
                .ShouldBe(Enumerable.Range(1, 3).Select(n => ($"{seed.RecordingId}-{n}", n == 3 ? "failed" : "accepted")));
            token = body.GetProperty("continuationToken").GetString();
            pages++;
        }
        while (token is not null);
        pages.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task PagesEveryResultExactlyOnceAndFiltersByRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        var seen = new List<Guid>();
        string? token = null;
        do
        {
            using var response = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, "&pageSize=1" + (token is null ? "" : "&continuationToken=" + token)), ct);
            var (status, body) = await BodyAsync(response, ct);
            status.ShouldBe(HttpStatusCode.OK, body.ToString());
            seen.AddRange(body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("acceptedResultId").GetGuid()));
            token = body.GetProperty("continuationToken").GetString();
        }
        while (token is not null);
        seen.Order().ShouldBe(seed.SnapshotResults.Order());

        using var ranged = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, $"&matchStartMs=0&matchEndMs={SegmentMs}"), ct);
        var (_, rangedBody) = await BodyAsync(ranged, ct);
        rangedBody.GetProperty("results").GetArrayLength().ShouldBe(1);
        rangedBody.GetProperty("coverage").GetArrayLength().ShouldBe(1);

        using var badToken = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, "&continuationToken=!!"), ct);
        badToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesWithoutDisclosureAndAuditsForeignAndOutOfScopeReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var foreign = await other.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var unknown = await m.ListUpstreamResultsAsync(Guid.NewGuid(), Query(c), ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var wrongToken = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, token: c.Token + 1), ct);
        var (obsoleteStatus, obsolete) = await BodyAsync(wrongToken, ct);
        obsoleteStatus.ShouldBe(HttpStatusCode.Conflict);
        obsolete.GetProperty("code").GetString().ShouldBe("attempt-obsolete");

        using var outOfScope = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, capability: "cap-other"), ct);
        var (scopeStatus, scope) = await BodyAsync(outOfScope, ct);
        scopeStatus.ShouldBe(HttpStatusCode.BadRequest);
        scope.GetProperty("code").GetString().ShouldBe("upstream-dependency-unknown");
        scope.ToString().ShouldNotContain("cap-low");

        foreach (var invalid in new[] { "&segmentIds=a&matchStartMs=0&matchEndMs=10", "&pageSize=0", "&pageSize=101", "&matchStartMs=5" })
        {
            using var r = await m.ListUpstreamResultsAsync(c.Attempt, Query(c, invalid), ct);
            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest, invalid);
        }

        using var missingToken = await m.ListUpstreamResultsAsync(c.Attempt, "capabilityId=cap-low&capabilityVersion=1.0.0&schemaVersion=1.0.0", ct);
        missingToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await TextAsync(db, "SELECT string_agg(reason_code, ',' ORDER BY reason_code) FROM socalytics.security_audit_event WHERE event_type = 'analysis.upstream-read-refused'", ct))
            .ShouldBe("foreign-attempt,out-of-scope");

        await using (var conn = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn,
                "UPDATE socalytics.analysis_execution_attempts SET state = 'Stale', closed_at = now(), close_reason = 'lease-expired' WHERE id = @a; UPDATE socalytics.analysis_logical_jobs SET current_attempt_id = NULL WHERE id = @j",
                ct, ("a", c.Attempt), ("j", seed.HighJob));
        }

        using var fenced = await m.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        fenced.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NoLogOrAuditRecordContainsGrantUrls()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        var (_, body) = await BodyAsync(response, ct);
        var url = body.GetProperty("results")[0].GetProperty("grants")[0].GetProperty("url").GetString()!;
        url.ShouldContain("X-Amz-Signature");
        using var foreign = await other.ListUpstreamResultsAsync(c.Attempt, Query(c), ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        foreach (var entry in host.Factory.Logs.Entries.ToList())
        {
            var text = entry.Message + entry.Exception + string.Join(' ', entry.State.Values);
            text.ShouldNotContain("X-Amz-Signature");
            text.ShouldNotContain(url);
        }

        var audit = await TextAsync(db, "SELECT string_agg(details::text || coalesce(reason_code, ''), ' ') FROM socalytics.security_audit_event", ct);
        audit.ShouldNotBeNull();
        audit.ShouldNotContain("X-Amz-Signature");
        audit.ShouldNotContain("http");
    }

    [Fact]
    public async Task UnregisteringManagerReadsAndRevokedManagerIsRefusedByAuthentication()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Real);
        await host.StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);
        using var claim = await manager.ClaimAsync(seed.HighJob, seed.HighOccurrence, "real-1", ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var path = $"/api/v1/analysis/attempts/{a.GetProperty("attemptId").GetGuid()}/upstream-results?fencingToken={a.GetProperty("fencingToken").GetInt64()}&capabilityId=cap-low&capabilityVersion=1.0.0&schemaVersion=1.0.0";

        using var begin = await manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/unregistration", null, null, ct);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
        using var draining = await manager.SendAsync(HttpMethod.Get, path, null, null, ct);
        draining.StatusCode.ShouldBe(HttpStatusCode.OK, await draining.Content.ReadAsStringAsync(ct));

        await manager.RevokeAsync(ct);
        using var revoked = await manager.SendAsync(HttpMethod.Get, path, null, null, ct);
        revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
