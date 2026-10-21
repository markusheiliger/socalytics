using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class RestartMatrixTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    private sealed record ReadyJob(Guid Id, Guid Node, Guid Occurrence, Guid Team, Guid Match, string NodeKey);

    private sealed class Driver(AnalysisTestHost host, IsolatedDatabase db, NatsContainerFixture nats, RustFsContainerFixture rustFs, bool restart)
    {
        private readonly Guid _manager = Guid.NewGuid();

        public int Restarts { get; private set; }

        public async Task PointAsync(CancellationToken ct)
        {
            if (restart)
            {
                await host.RestartAsync(ct);
                Restarts++;
            }
        }

        public async Task<Guid> SeedAsync(CancellationToken ct)
        {
            var recordingVersion = Guid.NewGuid();
            var specs = new[] { ("s1", true), ("m1", false) };
            var records = specs.Select((n, i) => new WorkflowNodeRecord(
                Guid.CreateVersion7(), n.Item1, "cap-" + n.Item1, "1.0.0", CapabilityTier.LowLevel,
                n.Item2 ? ExecutionScope.Segment : ExecutionScope.Match, i, WorkflowNodeState.Waiting, null, default,
                "{\"analystProfileId\":\"profile\",\"policyVersions\":{}}")).ToList();
            var edges = new[] { new DependencyEdgeRecord(records[0].Id, records[1].Id, DependencyKind.Required, null) };
            await using var scope = host.Services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "wf", "1.0.0", ["cap-s1", "cap-m1"], "all-requested-capabilities",
                AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord("{\"segmentationPolicy\":{\"durationSeconds\":300}}", Digest), records, edges), ct);
            var jobs = new List<NewLogicalJob>();
            foreach (var record in records)
            {
                var segments = record.ExecutionScope == ExecutionScope.Segment ? new int?[] { 1, 2 } : [null];
                foreach (var number in segments)
                {
                    jobs.Add(new NewLogicalJob(
                        Guid.CreateVersion7(), runId, record.Id, record.ExecutionScope, number is null ? null : recordingVersion,
                        number is null ? null : Digest, number is null ? null : Digest, number, LogicalJobState.Waiting,
                        new LogicalJobSnapshotRecord(3, 60, 120, 600, Digest, Digest, null, "{\"cpu\":\"1\"}", "1.0.0")));
                }
            }

            await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(jobs, ct);
            await uow.CommitAsync(ct);
            return runId;
        }

        public async Task ApplyAsync(Guid runId, CancellationToken ct)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
            await scope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
            await uow.CommitAsync(ct);
        }

        public async Task PublishAsync(CancellationToken ct)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PublishOutboxBatch>().HandleAsync(ct);
        }

        public async Task RepublishAsync(CancellationToken ct)
        {
            await using var connection = new NATS.Client.Core.NatsConnection(NATS.Client.Core.NatsOpts.Default with { Url = nats.ConnectionString });
            var js = new NATS.Client.JetStream.NatsJSContext(connection);
            foreach (var m in await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct))
            {
                // No message id, so the duplicate is stored: ready-work is delivered at least twice.
                await js.PublishAsync(m.Subject, m.Data, cancellationToken: ct);
            }
        }

        public async Task ExpireAsync(CancellationToken ct)
        {
            host.Time.Advance(TimeSpan.FromSeconds(300));
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct);
        }

        public async Task<ReadyJob?> NextReadyAsync(Guid runId, CancellationToken ct)
        {
            await using var sql = await OpenAsync(db, ct);
            return await sql.QueryFirstOrDefaultAsync<ReadyJob>(
                """
                SELECT j.id AS Id, j.node_id AS Node, j.current_readiness_occurrence_id AS Occurrence, r.team_id AS Team, r.match_id AS Match, n.node_key AS NodeKey
                FROM socalytics.analysis_logical_jobs j
                JOIN socalytics.analysis_runs r ON r.id = j.run_id
                JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
                WHERE j.run_id = @runId AND j.state = 'Ready' AND j.current_readiness_occurrence_id IS NOT NULL
                ORDER BY n.node_key, j.segment_number NULLS FIRST LIMIT 1
                """, new { runId });
        }

        public async Task<(Guid Attempt, long Token, int Number)> ClaimAsync(ReadyJob job, string key, CancellationToken ct)
        {
            using var client = new TestManagerClient(host.Factory, _manager);
            using var response = await client.ClaimAsync(job.Id, job.Occurrence, key, ct: ct);
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(ct));
            var b = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
            return (b.GetProperty("attemptId").GetGuid(), b.GetProperty("fencingToken").GetInt64(), b.GetProperty("attemptNumber").GetInt32());
        }

        public async Task HeartbeatAsync(ReadyJob job, (Guid Attempt, long Token, int Number) a, CancellationToken ct)
        {
            using var client = new TestManagerClient(host.Factory, _manager);
            using var response = await client.HeartbeatAsync(a.Attempt, job.Id, a.Token, ct);
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(ct));
        }

        public async Task CompleteAsync(Guid runId, ReadyJob job, (Guid Attempt, long Token, int Number) a, CancellationToken ct)
        {
            var upstream = new JsonArray();
            await using (var sql = await OpenAsync(db, ct))
            {
                var inputs = await sql.QueryFirstOrDefaultAsync<string>(
                    "SELECT inputs::text FROM socalytics.analysis_logical_job_inputs WHERE logical_job_id = @id", new { id = job.Id });
                if (inputs is not null)
                {
                    foreach (var e in JsonDocument.Parse(inputs).RootElement.EnumerateArray())
                    {
                        foreach (var id in e.GetProperty("acceptedResultIds").EnumerateArray())
                        {
                            upstream.Add(id.GetGuid());
                        }
                    }
                }
            }

            var body = new JsonObject
            {
                ["contractVersion"] = "1.0.0",
                ["idempotencyKey"] = "done-" + job.Id,
                ["teamId"] = job.Team,
                ["matchId"] = job.Match,
                ["runId"] = runId,
                ["nodeId"] = job.Node,
                ["logicalJobId"] = job.Id,
                ["attemptId"] = a.Attempt,
                ["attemptNumber"] = a.Number,
                ["fencingToken"] = a.Token,
                ["outcome"] = "succeeded",
                ["capability"] = new JsonObject { ["id"] = "cap-" + job.NodeKey, ["version"] = "1.0.0" },
                ["schemaVersion"] = "1.0.0",
                ["executedRuntime"] = new JsonObject
                {
                    ["runtimeProfile"] = "cpu", ["runtimeVersion"] = "1.0", ["operatingSystem"] = "linux",
                    ["architecture"] = "x64", ["accelerators"] = new JsonArray(),
                },
                ["executedImageDigest"] = Digest,
                ["executedModelDigest"] = null,
                ["acceptedUpstreamResultIds"] = upstream,
                ["completedAt"] = "2026-01-01T00:00:00Z",
                ["correlation"] = new JsonObject { ["correlationId"] = job.Id },
                ["result"] = new JsonObject { ["manifestReference"] = "results/" + job.Id + ".json", ["manifestChecksum"] = Digest },
            };
            await ResultObjectsHelper.ApplyAsync(db, rustFs, a.Attempt, body, upstream.Select(n => n!.GetValue<Guid>()).ToList(), ct);
            using var client = host.Factory.CreateApiClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{a.Attempt}/completion")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, _manager.ToString());
            using var response = await client.SendAsync(request, ct);
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(ct));
        }

        public async Task<string> RunAsync(CancellationToken ct)
        {
            var runId = await SeedAsync(ct);
            await PointAsync(ct);
            await ApplyAsync(runId, ct);
            await PointAsync(ct);
            await ApplyAsync(runId, ct);
            await PointAsync(ct);
            await PublishAsync(ct);
            await PointAsync(ct);

            var first = (await NextReadyAsync(runId, ct)).ShouldNotBeNull();
            await ClaimAsync(first, "abandoned", ct);
            await PointAsync(ct);
            await ExpireAsync(ct);
            await PointAsync(ct);
            await ApplyAsync(runId, ct);
            await PublishAsync(ct);
            await PointAsync(ct);
            await ApplyAsync(runId, ct);
            await PointAsync(ct);

            for (var i = 0; i < 10 && await NextReadyAsync(runId, ct) is { } job; i++)
            {
                var attempt = await ClaimAsync(job, "claim-" + i, ct);
                var again = await ClaimAsync(job, "claim-" + i, ct);
                again.Attempt.ShouldBe(attempt.Attempt);
                await PointAsync(ct);
                await HeartbeatAsync(job, attempt, ct);
                await PointAsync(ct);
                await CompleteAsync(runId, job, attempt, ct);
                await CompleteAsync(runId, job, attempt, ct);
                await PointAsync(ct);
                await ApplyAsync(runId, ct);
                await PublishAsync(ct);
                await RepublishAsync(ct);
                await PointAsync(ct);
                await PublishAsync(ct);
            }

            return await SummaryAsync(runId, ct);
        }

        private async Task<string> SummaryAsync(Guid runId, CancellationToken ct)
        {
            await using var sql = await OpenAsync(db, ct);
            var jobs = await sql.QueryAsync<string>(
                """
                SELECT n.node_key || '/' || COALESCE(j.segment_number::text, '-') || '=' || j.state
                    || ' attempts=' || (SELECT count(*) FROM socalytics.analysis_execution_attempts a WHERE a.logical_job_id = j.id)
                    || ' accepted=' || (SELECT count(*) FROM socalytics.analysis_accepted_results r WHERE r.logical_job_id = j.id)
                    || ' ready-work=' || (SELECT count(*) FROM socalytics.outbox_messages o WHERE o.logical_job_id = j.id AND o.message_type = 'analysis.ready-work' AND o.state <> 'discarded')
                FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
                WHERE j.run_id = @runId ORDER BY n.node_key, j.segment_number
                """, new { runId });
            var run = await sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_runs WHERE id = @runId", new { runId });
            var runs = await sql.QuerySingleAsync<int>("SELECT count(*) FROM socalytics.analysis_runs");
            var text = string.Join('\n', jobs.Append("run=" + run).Append("runs=" + runs));
            return Regex.Replace(text, "[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", "<id>");
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(IsolatedDatabase db, CancellationToken ct)
    {
        var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs);
        await host.StartAsync(ct);
        return (db, host);
    }

    [Fact]
    public async Task Restarts_at_every_lifecycle_point_match_the_uninterrupted_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var (refDb, refHost) = await StartAsync(ct);
        await using var _ = refDb;
        await using var __ = refHost;
        var (db, host) = await StartAsync(ct);
        await using var ___ = db;
        await using var ____ = host;

        var expected = await new Driver(refHost, refDb, nats, rustFs, restart: false).RunAsync(ct);
        var driver = new Driver(host, db, nats, rustFs, restart: true);
        var actual = await driver.RunAsync(ct);

        driver.Restarts.ShouldBeGreaterThanOrEqualTo(20);
        actual.ShouldBe(expected);
        actual.ShouldContain("run=Completed");
        actual.ShouldContain("m1/-=Completed attempts=1 accepted=1 ready-work=1");
        actual.ShouldContain("runs=1");
    }
}
