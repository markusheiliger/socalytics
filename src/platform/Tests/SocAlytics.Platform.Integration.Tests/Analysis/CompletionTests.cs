using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
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

    private static async Task<string> RowsAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : r.GetValue(i) is Array a ? string.Join(",", a.Cast<object>()) : r.GetValue(i).ToString())));
        }

        return string.Join("\n", rows);
    }

    private static async Task<(Guid Attempt, long Token, int Number)> ClaimNextAsync(TestManagerClient m, Seed s, IsolatedDatabase db, string key, CancellationToken ct)
    {
        var occ = Guid.Parse((await RowsAsync(db, "SELECT current_readiness_occurrence_id FROM socalytics.analysis_logical_jobs", ct)));
        using var r = await m.ClaimAsync(s.Job, occ, key, ct: ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync(ct));
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        return (b.GetProperty("attemptId").GetGuid(), b.GetProperty("fencingToken").GetInt64(), b.GetProperty("attemptNumber").GetInt32());
    }

    private static JsonObject Clone(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, CancellationToken ct) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;

    private static async Task<int> ExpireAsync(AnalysisTestHost host, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct);
    }

    private const string StateSql =
        "SELECT (SELECT string_agg(id || ':' || state || ':' || coalesce(close_reason, ''), ',' ORDER BY attempt_number) FROM socalytics.analysis_execution_attempts) || '/' || " +
        "(SELECT string_agg(state, ',') FROM socalytics.analysis_logical_jobs) || '/' || (SELECT count(*) FROM socalytics.analysis_accepted_results) || '/' || (SELECT count(*) FROM socalytics.analysis_completion_outcomes)";

    [Fact]
    public async Task ReplayWithReorderedPropertiesReturnsStoredAcceptedOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "done-1", true);
        using var first = await CompleteAsync(host.Factory, m, attempt, body, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await ReadAsync(first, ct);
        var before = await RowsAsync(db, StateSql, ct);

        var reordered = new JsonObject();
        foreach (var p in body.Reverse())
        {
            reordered[p.Key] = JsonNode.Parse(p.Value!.ToJsonString());
        }

        using var replay = await CompleteAsync(host.Factory, m, attempt, reordered, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync(ct));
        var replayBody = await ReadAsync(replay, ct);
        replayBody.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        replayBody.GetProperty("acceptedResultId").GetGuid().ShouldBe(firstBody.GetProperty("acceptedResultId").GetGuid());
        (await RowsAsync(db, StateSql, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task SameKeyWithDifferentContentIsReusedKeyConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "done-1", true);
        using var first = await CompleteAsync(host.Factory, m, attempt, body, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await RowsAsync(db, "SELECT * FROM socalytics.analysis_completion_outcomes", ct);

        var changed = Clone(body);
        changed["result"]!["manifestReference"] = "results/other.json";
        using var r = await CompleteAsync(host.Factory, m, attempt, changed, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.Content.ReadAsStringAsync(ct)).ShouldContain("idempotency-key-reused");
        (await RowsAsync(db, "SELECT * FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe(before);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results WHERE manifest_reference = 'results/m1.json'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ReplayOfFailureRecordedReturnsStoredOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "fail-1", false);
        using var first = await CompleteAsync(host.Factory, m, attempt, body, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await RowsAsync(db, StateSql, ct);

        using var replay = await CompleteAsync(host.Factory, m, attempt, Clone(body), ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var b = await ReadAsync(replay, ct);
        b.GetProperty("outcome").GetString().ShouldBe("failure-recorded");
        b.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        (await RowsAsync(db, StateSql, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task InvalidTokensAndIdentitiesAreRejectedWithoutChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (first, firstToken, firstNumber) = await ClaimAsync(m, s, "c1", ct);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await ExpireAsync(host, ct)).ShouldBe(1);
        var (second, secondToken, secondNumber) = await ClaimNextAsync(m, s, db, "c2", ct);
        var before = await RowsAsync(db, StateSql, ct);

        using var other = new TestManagerClient(host.Factory);
        var cases = new List<(string Name, TestManagerClient Client, Guid Path, JsonObject Body)>
        {
            ("stale", m, first, Body(s, first, firstToken, firstNumber, "k-stale", true)),
            ("superseded", m, second, Body(s, second, firstToken, secondNumber, "k-superseded", true)),
            ("fabricated", m, second, Body(s, second, secondToken + 1000, secondNumber, "k-fab", true)),
            ("wrong-number", m, second, Body(s, second, secondToken, firstNumber, "k-num", true)),
            ("wrong-manager", other, second, Body(s, second, secondToken, secondNumber, "k-mgr", true)),
        };
        var unknown = Guid.NewGuid();
        cases.Add(("wrong-attempt", m, unknown, Body(s, unknown, secondToken, secondNumber, "k-att", true)));
        foreach (var (name, client, path, body) in cases)
        {
            using var r = await CompleteAsync(host.Factory, client, path, body, ct);
            r.StatusCode.ShouldBe(HttpStatusCode.Conflict, name);
            (await r.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");
            (await RowsAsync(db, StateSql, ct)).ShouldBe(before, name);
        }
    }

    [Fact]
    public async Task CompletionFirstMakesExpiryANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        using var ok = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "done-1", true), ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Time.Advance(TimeSpan.FromMinutes(5));

        (await ExpireAsync(host, ct)).ShouldBe(0);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Accepted'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiryFirstMakesCompletionObsolete()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await ExpireAsync(host, ct)).ShouldBe(1);

        using var late = await CompleteAsync(host.Factory, m, attempt, Body(s, attempt, token, number, "done-1", true), ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await late.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(0);

        var racing = await ClaimNextAsync(m, s, db, "c2", ct);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var completion = CompleteAsync(host.Factory, m, racing.Attempt, Body(s, racing.Attempt, racing.Token, racing.Number, "done-2", true), ct);
        var expiry = ExpireAsync(host, ct);
        await Task.WhenAll(completion, expiry);
        using var racedResponse = await completion;
        (await expiry).ShouldBe(1);
        racedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task TenConcurrentIdenticalCompletionsCreateOneAcceptedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var (attempt, token, number) = await ClaimAsync(m, s, "c1", ct);
        var body = Body(s, attempt, token, number, "done-1", true);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            using var r = await CompleteAsync(host.Factory, m, attempt, Clone(body), ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            return (r.StatusCode, Body: r.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(text).RootElement : default, Text: text);
        }, ct)));
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => r.Text).ShouldBeEmpty();
        results.Select(r => r.Body.GetProperty("acceptedResultId").GetGuid()).Distinct().Count().ShouldBe(1);
        results.Count(r => !r.Body.GetProperty("replayed").GetBoolean()).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(1);
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe(1);
    }

    private static async Task<(IReadOnlyList<ExecutionAttemptRecord> Attempts, IReadOnlyList<AcceptedResultRecord> Results)> LoadAsync(
        AnalysisTestHost host, Seed s, IReadOnlyList<Guid> attemptIds, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var tx = await uow.BeginAsync(ct);
        var attempts = new List<ExecutionAttemptRecord>();
        foreach (var id in attemptIds)
        {
            attempts.Add((await scope.ServiceProvider.GetRequiredService<IExecutionAttemptStore>().LockAsync(id, ct))!);
        }

        var results = await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().ListByRunAsync(s.Run, ct);
        return (attempts, results);
    }

    [Fact]
    public async Task AttemptsAndAcceptedResultsSurviveHostRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var s = await SeedReadyJobAsync(db, ct);
        var upstream = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await c.OpenAsync(ct);
            foreach (var (u, i) in upstream.Select((u, i) => (u, i)))
            {
                await using var up = new NpgsqlCommand(
                    """
                    INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
                    VALUES (@j, @run, 'up' || @i, 'cap-a', '1.0.0', 'high-level', 'match', 1 + @i, 'Completed', now());
                    INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
                    VALUES (@j, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb);
                    INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
                    VALUES (@j, @run, @j, 'match', 'Completed', now());
                    INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
                    VALUES (@j, @j, 1, 'initial', now());
                    INSERT INTO socalytics.analysis_execution_attempts (id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at, closed_at)
                    VALUES (@j, @j, @j, 1, @j, nextval('socalytics.analysis_fencing_token_seq'), 'Accepted', now(), now(), now(), now());
                    INSERT INTO socalytics.analysis_accepted_results (id, logical_job_id, run_id, node_id, attempt_id, manifest_reference, manifest_checksum, capability_id, capability_version, schema_version, analyst_profile_digest, image_digest, policy_versions, executed_runtime, accepted_at)
                    VALUES (@u, @j, @run, @j, @j, 'r.json', @d, 'cap-a', '1.0.0', '1.0.0', @d, @d, '{}'::jsonb, '{}'::jsonb, now() + @i * interval '1 second');
                    """, c);
                up.Parameters.AddWithValue("j", Guid.NewGuid());
                up.Parameters.AddWithValue("u", u);
                up.Parameters.AddWithValue("run", s.Run);
                up.Parameters.AddWithValue("node", s.Node);
                up.Parameters.AddWithValue("d", Digest);
                up.Parameters.AddWithValue("i", i);
                await up.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand(
                "INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at) VALUES (@j, @i::jsonb, now())", c);
            cmd.Parameters.AddWithValue("j", s.Job);
            cmd.Parameters.AddWithValue("i", JsonSerializer.Serialize(new[] { new { upstreamNodeKey = "m1", edgeKind = "required", status = "accepted", acceptedResultIds = upstream } }));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var m = new TestManagerClient(host.Factory);
        var a1 = await ClaimAsync(m, s, "c1", ct);
        var failed = Body(s, a1.Attempt, a1.Token, a1.Number, "fail-1", false);
        failed["acceptedUpstreamResultIds"] = new JsonArray(upstream.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray());
        using var f = await CompleteAsync(host.Factory, m, a1.Attempt, failed, ct);
        f.StatusCode.ShouldBe(HttpStatusCode.OK, await f.Content.ReadAsStringAsync(ct));
        var a2 = await ClaimNextAsync(m, s, db, "c2", ct);
        var good = Body(s, a2.Attempt, a2.Token, a2.Number, "done-1", true);
        good["acceptedUpstreamResultIds"] = new JsonArray(upstream.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray());
        using var ok = await CompleteAsync(host.Factory, m, a2.Attempt, good, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(ct));

        var ids = new[] { a1.Attempt, a2.Attempt };
        var before = await LoadAsync(host, s, ids, ct);
        before.Attempts.Select(a => a.State).ShouldBe([ExecutionAttemptState.Failed, ExecutionAttemptState.Accepted]);
        before.Results.Count.ShouldBe(4);
        before.Results.Single(r => r.AttemptId == a2.Attempt).UpstreamResultIds.ShouldBe(upstream);

        await host.RestartAsync(ct);
        var after = await LoadAsync(host, s, ids, ct);

        after.Attempts.ShouldBe(before.Attempts);
        after.Results.ShouldBe(before.Results, new ResultComparer());
        var (x, y) = (before.Results.Single(r => r.AttemptId == a2.Attempt), after.Results.Single(r => r.AttemptId == a2.Attempt));
        (x with { UpstreamResultIds = [] }).ShouldBe(y with { UpstreamResultIds = [] });
        y.UpstreamResultIds.ShouldBe(x.UpstreamResultIds);
    }

    private sealed class ResultComparer : IEqualityComparer<AcceptedResultRecord>
    {
        public bool Equals(AcceptedResultRecord? a, AcceptedResultRecord? b) =>
            a is not null && b is not null && (a with { UpstreamResultIds = [] }) == (b with { UpstreamResultIds = [] })
            && a.UpstreamResultIds.SequenceEqual(b.UpstreamResultIds);

        public int GetHashCode(AcceptedResultRecord r) => r.Id.GetHashCode();
    }

    private sealed class VersionFlag { public volatile string? Version; }

    private sealed class VersionedResolver(IWorkflowDefinitionResolver inner, VersionFlag flag) : IWorkflowDefinitionResolver
    {
        public async Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken cancellationToken)
        {
            var resolved = await inner.ResolveDefaultAsync(cancellationToken);
            return flag.Version is { } v && resolved is WorkflowResolution.ResolvedResult r
                ? WorkflowResolution.Resolved(r.Definition with { WorkflowVersion = v })
                : resolved;
        }
    }

    [Fact]
    public async Task NewWorkflowVersionCreatesIndependentSecondRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var flag = new VersionFlag();
        await using var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
        {
            services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
                o.Schemes.Single(x => x.Name == SocAlytics.Platform.Api.Registry.AnalystManagerDPoPDefaults.AuthenticationScheme).HandlerType = typeof(TestManagerAuthenticationHandler));
            var original = services.Last(d => d.ServiceType == typeof(IWorkflowDefinitionResolver));
            services.Remove(original);
            services.AddSingleton<IWorkflowDefinitionResolver>(sp => new VersionedResolver(
                (IWorkflowDefinitionResolver)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), flag));
        });
        var set = await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
        async Task<RecordingsFinalizedNotification> Notify()
        {
            await using var scope = setup.Factory.Services.CreateAsyncScope();
            var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
            return new RecordingsFinalizedNotification(
                Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
                lineage.Members.Select(m => new FinalizedNotificationMember(m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList());
        }

        async Task HandleAsync()
        {
            await using var scope = setup.Factory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(await Notify(), ct))
                .ShouldBeOfType<NotificationOutcome.RunCreated>();
        }

        await HandleAsync();
        var runId = Guid.Parse((await RowsAsync(setup.Db, "SELECT id FROM socalytics.analysis_runs", ct)));
        var row = (await RowsAsync(setup.Db,
            $"SELECT j.id, j.node_id, j.current_readiness_occurrence_id FROM socalytics.analysis_logical_jobs j WHERE j.run_id = '{runId}' AND j.state = 'Ready'", ct)).Split('|');
        var seed = new Seed(runId, Guid.Parse(row[1]), Guid.Parse(row[0]), Guid.Parse(row[2]), setup.Hierarchy.TeamId, setup.Hierarchy.MatchId);
        using var manager = new TestManagerClient(setup.Factory);
        var (attempt, token, number) = await ClaimAsync(manager, seed, "c1", ct);
        var body = Body(seed, attempt, token, number, "done-1", true);
        body["capability"] = new JsonObject { ["id"] = "dev-segment-capability", ["version"] = "1.0.0" };
        body["schemaVersion"] = "1.0.0";
        body["executedImageDigest"] = "sha-256:" + new string('3', 64);
        body["executedModelDigest"] = null;
        using var ok = await CompleteAsync(setup.Factory, manager, attempt, body, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(ct));
        const string FirstRunSql = "SELECT * FROM socalytics.analysis_accepted_results";
        var acceptedBefore = await RowsAsync(setup.Db, FirstRunSql, ct);
        var runBefore = await RowsAsync(setup.Db, $"SELECT * FROM socalytics.analysis_runs WHERE id = '{runId}'", ct);
        var firstJobsBefore = await RowsAsync(setup.Db, $"SELECT id, state FROM socalytics.analysis_logical_jobs WHERE run_id = '{runId}' ORDER BY id", ct);
        acceptedBefore.ShouldNotBeEmpty();

        flag.Version = "2";
        await HandleAsync();

        (await setup.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(2);
        var second = Guid.Parse(await RowsAsync(setup.Db, $"SELECT id FROM socalytics.analysis_runs WHERE id <> '{runId}'", ct));
        (await setup.ScalarAsync($"SELECT count(*) FROM socalytics.analysis_workflow_nodes WHERE run_id = '{second}' AND state NOT IN ('Waiting', 'Active')", ct)).ShouldBe(0);
        (await setup.ScalarAsync($"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE run_id = '{second}' AND state = 'Completed'", ct)).ShouldBe(0);
        (await setup.ScalarAsync($"SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = '{second}'", ct)).ShouldBe(0);
        (await setup.ScalarAsync(
            $"SELECT count(*) FROM socalytics.analysis_logical_job_inputs i JOIN socalytics.analysis_logical_jobs j ON j.id = i.logical_job_id WHERE j.run_id = '{second}' AND i.inputs::text LIKE '%' || (SELECT id::text FROM socalytics.analysis_accepted_results LIMIT 1) || '%'", ct)).ShouldBe(0);
        (await RowsAsync(setup.Db, FirstRunSql, ct)).ShouldBe(acceptedBefore);
        (await RowsAsync(setup.Db, $"SELECT * FROM socalytics.analysis_runs WHERE id = '{runId}'", ct)).ShouldBe(runBefore);
        (await RowsAsync(setup.Db, $"SELECT id, state FROM socalytics.analysis_logical_jobs WHERE run_id = '{runId}' ORDER BY id", ct)).ShouldBe(firstJobsBefore);
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
