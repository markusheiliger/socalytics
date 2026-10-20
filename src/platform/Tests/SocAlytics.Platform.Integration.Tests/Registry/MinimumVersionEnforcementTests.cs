using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.NKeys;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.Analysis;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class MinimumVersionEnforcementTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private const string Code = "analyst-manager-version-below-minimum";
    private const string MinimumKey = "Registry:ManagerVersion:Minimum";
    private const string StatusPath = "/api/v1/analyst-managers/self/status";
    private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // The pairing body of the registry test host reports Manager version 1.0.0.
    private const string PairingVersion = "1.0.0";

    private sealed record Setup(IsolatedDatabase Db, AnalysisTestHost Host, RegisteredAnalystManagerClient Manager) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            await Host.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Real);
        await host.StartAsync(ct);
        var manager = await RegisteredAnalystManagerClient.RegisterAsync(host, db, ct);
        return new Setup(db, host, manager);
    }

    private static void SetMinimum(Setup s, string? value)
    {
        var root = (IConfigurationRoot)s.Host.Services.GetRequiredService<IConfiguration>();
        root[MinimumKey] = value;
        try
        {
            root.Reload();
        }
        catch (Exception ex) when (ex is OptionsValidationException || ex is AggregateException { InnerException: OptionsValidationException })
        {
            // The provider keeps the last valid minimum when a reload is invalid.
        }
    }

    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<Guid> GuidAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task StoreReportedVersionAsync(Setup s, string version, CancellationToken ct)
    {
        var id = await GuidAsync(s.Db, "SELECT id FROM socalytics.analyst_manager_registration", ct);
        await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analyst_manager_status
                (registration_id, operating_state, active_analysts, max_analysts, manager_version, received_at, interval_seconds)
            VALUES (@id, 'running', 0, 2, @v, now(), 60)
            ON CONFLICT (registration_id) DO UPDATE SET manager_version = @v, received_at = now();
            """, c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("v", version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task<HttpResponseMessage> ReportAsync(Setup s, string version, CancellationToken ct) =>
        s.Manager.SendAsync(HttpMethod.Post, StatusPath, new
        {
            operatingState = "running",
            pausedUntil = (string?)null,
            runtimeUnavailableReason = (string?)null,
            activeAnalysts = 0,
            maxAnalysts = 2,
            managerVersion = version,
        }, null, ct);

    private static async Task ReportOkAsync(Setup s, string version, CancellationToken ct)
    {
        using var r = await ReportAsync(s, version, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string minimum, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("code").GetString().ShouldBe(Code);
        body.GetProperty("minimumManagerVersion").GetString().ShouldBe(minimum);
    }

    private static async Task AssertNotRefusedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        text.ShouldNotContain(Code);
    }

    private static async Task<(Guid Attempt, long Token)> ClaimOkAsync(Setup s, Guid job, Guid occ, string key, CancellationToken ct)
    {
        using var r = await s.Manager.ClaimAsync(job, occ, key, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync(ct));
        var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        return (b.GetProperty("attemptId").GetGuid(), b.GetProperty("fencingToken").GetInt64());
    }

    private static MeterListener ListenForRefusals(List<string?> operations)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == AnalysisMetrics.MeterName && i.Name == "socalytics.analysis.manager_version_refusals")
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (operations)
            {
                operations.Add(tags.ToArray().Where(t => t.Key == "operation").Select(t => t.Value?.ToString()).SingleOrDefault());
            }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task Refused_claim_creates_no_attempt_and_the_job_stays_claimable_at_the_minimum()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(s.Db, "Ready", ct);
        var refusals = new List<string?>();
        using var listener = ListenForRefusals(refusals);
        SetMinimum(s, "1.1.0");

        // No report yet: the pairing version 1.0.0 applies.
        using var refused = await s.Manager.ClaimAsync(job, occ, "k1", ct);
        await AssertRefusedAsync(refused, "1.1.0", ct);
        (await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(0);
        (await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE state = 'Ready'", ct)).ShouldBe(1);
        lock (refusals)
        {
            refusals.ShouldBe(["claim"]);
        }

        // The latest report applies afterwards, in both directions.
        await ReportOkAsync(s, "1.0.5", ct);
        using var stillBelow = await s.Manager.ClaimAsync(job, occ, "k1", ct);
        await AssertRefusedAsync(stillBelow, "1.1.0", ct);
        await ReportOkAsync(s, "1.1.0", ct);
        var claim = await ClaimOkAsync(s, job, occ, "k1", ct);
        claim.Attempt.ShouldNotBe(Guid.Empty);
        (await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Malformed_reported_version_counts_as_below()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(s.Db, "Ready", ct);
        await StoreReportedVersionAsync(s, "2.0", ct);
        SetMinimum(s, "1.0.0");

        using var refused = await s.Manager.ClaimAsync(job, occ, "k1", ct);
        await AssertRefusedAsync(refused, "1.0.0", ct);
    }

    [Fact]
    public async Task Refused_queue_credentials_record_nothing_and_are_counted_without_logging_a_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var refusals = new List<string?>();
        using var listener = ListenForRefusals(refusals);
        SetMinimum(s, "1.1.0");
        var key = KeyPair.CreatePair(PrefixByte.User).GetPublicKey();

        using var refused = await s.Manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/queue-credentials",
            new { userPublicKey = key, capabilities = new[] { new { id = "cap-a", version = "1.0.0" } } }, null, ct);
        await AssertRefusedAsync(refused, "1.1.0", ct);
        (await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_queue_credential", ct)).ShouldBe(0);
        lock (refusals)
        {
            refusals.ShouldBe(["queue-credentials"]);
        }

        var entry = s.Host.Factory.Logs.Entries.Single(e => e.Category.EndsWith("ManagerVersionGate", StringComparison.Ordinal));
        entry.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        entry.Message.ShouldContain("1.1.0");
        entry.Message.ShouldContain(PairingVersion);
        entry.Message.ShouldNotContain("DPoP");
        entry.State.Keys.ShouldNotContain(k => k.Contains("token", StringComparison.OrdinalIgnoreCase) || k.Contains("proof", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Operations_on_an_attempt_claimed_before_the_minimum_was_raised_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(s.Db, "Ready", ct);
        var (attempt, token) = await ClaimOkAsync(s, job, occ, "k1", ct);
        SetMinimum(s, "1.1.0");

        using var replay = await s.Manager.ClaimAsync(job, occ, "k1", ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync(ct));
        var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(ct)).RootElement;
        replayBody.GetProperty("attemptId").GetGuid().ShouldBe(attempt);
        replayBody.GetProperty("replayed").GetBoolean().ShouldBeTrue();

        using var heartbeat = await s.Manager.SendAsync(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/heartbeats",
            new { logicalJobId = job, fencingToken = token, observedAt = s.Host.Time.GetUtcNow(), executionState = "running" }, null, ct);
        heartbeat.StatusCode.ShouldBe(HttpStatusCode.OK, await heartbeat.Content.ReadAsStringAsync(ct));

        using var grants = await s.Manager.SendAsync(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/result-grants",
            new { fencingToken = token, objects = new[] { new { name = "facts.json", sizeBytes = 100, digest = Digest } } }, null, ct);
        grants.StatusCode.ShouldBe(HttpStatusCode.OK, await grants.Content.ReadAsStringAsync(ct));

        using var upstream = await s.Manager.SendAsync(HttpMethod.Get,
            $"/api/v1/analysis/attempts/{attempt}/upstream-results?fencingToken={token}&capabilityId=cap-a&capabilityVersion=1.0.0&schemaVersion=1.0.0", null, null, ct);
        await AssertNotRefusedAsync(upstream, ct);

        using var status = await ReportAsync(s, PairingVersion, ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK, await status.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("minimumManagerVersion").GetString().ShouldBe("1.1.0");

        using var depth = await s.Manager.SendAsync(HttpMethod.Get, "/api/v1/analyst-managers/self/queue-depth", null, null, ct);
        await AssertNotRefusedAsync(depth, ct);

        var run = await GuidAsync(s.Db, "SELECT run_id FROM socalytics.analysis_logical_jobs", ct);
        var node = await GuidAsync(s.Db, "SELECT node_id FROM socalytics.analysis_logical_jobs", ct);
        var team = await GuidAsync(s.Db, "SELECT team_id FROM socalytics.analysis_runs", ct);
        var match = await GuidAsync(s.Db, "SELECT match_id FROM socalytics.analysis_runs", ct);
        var completion = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["idempotencyKey"] = "done-1",
            ["teamId"] = team,
            ["matchId"] = match,
            ["runId"] = run,
            ["nodeId"] = node,
            ["logicalJobId"] = job,
            ["attemptId"] = attempt,
            ["attemptNumber"] = 1,
            ["fencingToken"] = token,
            ["outcome"] = "failed",
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
            ["failure"] = new JsonObject { ["category"] = "analyst-error" },
        };
        using var complete = await s.Manager.SendAsync(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion", completion, null, ct);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync(ct));

        using var begin = await s.Manager.SendAsync(HttpMethod.Post, "/api/v1/analyst-managers/self/unregistration", null, null, ct);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Reload_that_lowers_the_minimum_lifts_the_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(s.Db, "Ready", ct);
        SetMinimum(s, "1.1.0");
        using var refused = await s.Manager.ClaimAsync(job, occ, "k1", ct);
        await AssertRefusedAsync(refused, "1.1.0", ct);

        SetMinimum(s, "1.0.0");
        await ClaimOkAsync(s, job, occ, "k1", ct);
    }

    [Fact]
    public async Task Without_a_minimum_claims_behave_as_before()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(s.Db, "Ready", ct);
        var refusals = new List<string?>();
        using var listener = ListenForRefusals(refusals);
        await StoreReportedVersionAsync(s, "not-a-version", ct);

        await ClaimOkAsync(s, job, occ, "k1", ct);
        lock (refusals)
        {
            refusals.ShouldBeEmpty();
        }
    }
}
