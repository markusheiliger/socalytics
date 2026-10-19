using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class PartialCoverageReadinessTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
	private static readonly string Digest = "sha-256:" + new string('a', 64);

	private const int SegmentCount = 3;

	private sealed class Scenario(IsolatedDatabase db, AnalysisTestHost host, Guid runId) : IAsyncDisposable
	{
		public IsolatedDatabase Db { get; } = db;

		public AnalysisTestHost Host { get; } = host;

		public Guid RunId { get; } = runId;

		public async ValueTask DisposeAsync()
		{
			await Host.DisposeAsync();
			await Db.DisposeAsync();
		}
	}

	private async Task<Scenario> StartAsync(CoveragePolicy policy, int matchMaxAttempts, CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var host = new AnalysisTestHost(db, nats, rustFs);
		await host.StartAsync(ct);

		var recordingVersion = Guid.NewGuid();
		var nodes = new[] { ("s", ExecutionScope.Segment), ("m", ExecutionScope.Match) };
		var records = nodes.Select((n, i) => new WorkflowNodeRecord(
			Guid.CreateVersion7(), n.Item1, "cap-" + n.Item1, "1.0.0", CapabilityTier.LowLevel, n.Item2, i,
			WorkflowNodeState.Waiting, null, default, "{\"analystProfileId\":\"profile\",\"policyVersions\":{}}")).ToList();
		var edges = new List<DependencyEdgeRecord>
		{
			new(records[0].Id, records[1].Id, DependencyKind.Required, null) { CoveragePolicy = policy },
		};

		await using var scope = host.Services.CreateAsyncScope();
		var sp = scope.ServiceProvider;
		await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1.0.0", ["cap-s", "cap-m"], "all-requested-capabilities",
			AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord("{}", Digest), records, edges), ct);
		var jobs = new List<NewLogicalJob>();
		foreach (var record in records)
		{
			var segments = record.ExecutionScope == ExecutionScope.Segment ? Enumerable.Range(1, SegmentCount).Select(n => (int?)n).ToArray() : [null];
			var maxAttempts = record.ExecutionScope == ExecutionScope.Segment ? 1 : matchMaxAttempts;
			foreach (var number in segments)
			{
				jobs.Add(new NewLogicalJob(
					Guid.CreateVersion7(), runId, record.Id, record.ExecutionScope, number is null ? null : recordingVersion,
					number is null ? null : Digest, number is null ? null : Digest, number, LogicalJobState.Waiting,
					new LogicalJobSnapshotRecord(maxAttempts, 60, 120, 600, Digest, Digest, null, "{\"cpu\":\"1\",\"memory\":\"1Gi\"}", "1.0.0")));
			}
		}

		await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(jobs, ct);
		await uow.CommitAsync(ct);
		return new Scenario(db, host, runId);
	}

	private static async Task ApplyAsync(Scenario s, CancellationToken ct)
	{
		await using var scope = s.Host.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(s.RunId, ct);
		await scope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(s.RunId, ct);
		await uow.CommitAsync(ct);
	}

	private static async Task<NpgsqlConnection> OpenAsync(Scenario s, CancellationToken ct)
	{
		var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		return c;
	}

	private const string JobsOfNode =
		"SELECT j.id FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.run_id = @runId AND n.node_key = @key";

	private static async Task<Guid> JobAsync(Scenario s, string key, int? segment, CancellationToken ct)
	{
		await using var sql = await OpenAsync(s, ct);
		return await sql.QuerySingleAsync<Guid>(
			JobsOfNode + " AND j.segment_number IS NOT DISTINCT FROM @segment", new { s.RunId, key, segment });
	}

	private static async Task CompleteAsync(Scenario s, int segment, CancellationToken ct)
	{
		var id = await JobAsync(s, "s", segment, ct);
		await CompleteJobAsync(s, id, ct);
	}

	private static async Task CompleteJobAsync(Scenario s, Guid id, CancellationToken ct)
	{
		await using var sql = await OpenAsync(s, ct);
		await sql.ExecuteAsync(
			"""
			INSERT INTO socalytics.analysis_execution_attempts
				(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at, closed_at)
			SELECT @attemptId, j.id, j.current_readiness_occurrence_id,
				COALESCE((SELECT max(attempt_number) FROM socalytics.analysis_execution_attempts WHERE logical_job_id = j.id), 0) + 1,
				gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Accepted', now(), now(), now(), now()
			FROM socalytics.analysis_logical_jobs j WHERE j.id = @id;
			INSERT INTO socalytics.analysis_accepted_results
				(id, logical_job_id, run_id, node_id, attempt_id, manifest_reference, manifest_checksum, capability_id, capability_version, schema_version,
				 analyst_profile_digest, image_digest, policy_versions, executed_runtime, accepted_at)
			SELECT gen_random_uuid(), j.id, j.run_id, j.node_id, @attemptId, 'manifest/' || j.id, @digest, n.capability_id, n.capability_version, '1.0.0',
				@digest, @digest, '{}'::jsonb, '{}'::jsonb, now()
			FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.id = @id;
			UPDATE socalytics.analysis_logical_jobs SET state = 'Completed' WHERE id = @id;
			""",
			new { id, attemptId = Guid.NewGuid(), digest = Digest });
	}

	// Claims the job and lets the lease expire, which requeues it or, with its attempts used, fails it.
	private static async Task ClaimAndExpireAsync(Scenario s, Guid jobId, CancellationToken ct)
	{
		Guid occurrence;
		await using (var sql = await OpenAsync(s, ct))
		{
			occurrence = await sql.QuerySingleAsync<Guid>(
				"SELECT current_readiness_occurrence_id FROM socalytics.analysis_logical_jobs WHERE id = @jobId", new { jobId });
		}

		using (var manager = new TestManagerClient(s.Host.Factory))
		{
			using var claim = await manager.ClaimAsync(jobId, occurrence, "claim-" + Guid.NewGuid(), ct: ct);
			claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
		}

		s.Host.Time.Advance(TimeSpan.FromMinutes(5));
		await using var scope = s.Host.Services.CreateAsyncScope();
		(await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct)).ShouldBe(1);
	}

	private static async Task FailSegmentAsync(Scenario s, int segment, CancellationToken ct) =>
		await ClaimAndExpireAsync(s, await JobAsync(s, "s", segment, ct), ct);

	private static async Task<string> ScalarTextAsync(Scenario s, string query, CancellationToken ct)
	{
		await using var sql = await OpenAsync(s, ct);
		return await sql.QuerySingleAsync<string>(query, new { s.RunId });
	}

	private static Task<string> JobStateAsync(Scenario s, string key, CancellationToken ct) =>
		ScalarTextAsync(s, JobsOfNode.Replace("SELECT j.id", "SELECT j.state").Replace("@key", $"'{key}'") + " ORDER BY j.segment_number LIMIT 1", ct);

	private static Task<string> InputsAsync(Scenario s, CancellationToken ct) =>
		ScalarTextAsync(s, JobsOfNode.Replace("SELECT j.id", "SELECT COALESCE((SELECT i.inputs::text FROM socalytics.analysis_logical_job_inputs i WHERE i.logical_job_id = j.id), '')").Replace("@key", "'m'"), ct);

	private static async Task<int> ReadyWorkAsync(Scenario s, string key, CancellationToken ct)
	{
		await using var sql = await OpenAsync(s, ct);
		return await sql.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work' AND state <> 'discarded' AND logical_job_id IN ("
			+ JobsOfNode + ")", new { s.RunId, key });
	}

	private static Task<string> RunStateAsync(Scenario s, CancellationToken ct) =>
		ScalarTextAsync(s, "SELECT state FROM socalytics.analysis_runs WHERE id = @runId", ct);

	private static async Task<string> DigestAsync(Scenario s, CancellationToken ct)
	{
		await using var sql = await OpenAsync(s, ct);
		var rows = await sql.QueryAsync<string>(
			"""
			SELECT n.node_key || '/' || COALESCE(j.segment_number::text, '-') || '=' || j.state || '|' || COALESCE(j.failure_reason, '') || '|' || COALESCE(i.inputs::text, '')
			FROM socalytics.analysis_logical_jobs j
			JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
			LEFT JOIN socalytics.analysis_logical_job_inputs i ON i.logical_job_id = j.id
			WHERE j.run_id = @runId ORDER BY n.node_key, j.segment_number
			""", new { s.RunId });
		var nodes = await sql.QueryAsync<string>(
			"SELECT node_key || '=' || state FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId ORDER BY node_key", new { s.RunId });
		var run = await sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_runs WHERE id = @runId", new { s.RunId });
		var published = await sql.QueryAsync<string>(
			"""
			SELECT n.node_key || '/' || COALESCE(j.segment_number::text, '-') || ':' || count(*)
			FROM socalytics.outbox_messages o
			JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id
			JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
			WHERE o.run_id = @runId AND o.message_type = 'analysis.ready-work' AND o.state <> 'discarded'
			GROUP BY n.node_key, j.segment_number ORDER BY 1
			""", new { s.RunId });
		return string.Join('\n', rows.Concat(nodes).Append(run).Concat(published));
	}

	private static async Task<string> ReconcileAsync(Scenario s, CancellationToken ct)
	{
		await using var scope = s.Host.Services.CreateAsyncScope();
		await scope.ServiceProvider.GetRequiredService<ReconcileAnalysisPublicationHandler>().HandleAsync(ct);
		return await DigestAsync(s, ct);
	}

	private static MeterListener Listen(List<(string Name, long Value, Dictionary<string, object?> Tags)> sink)
	{
		var listener = new MeterListener
		{
			InstrumentPublished = (i, l) =>
			{
				if (i.Meter.Name == AnalysisMetrics.MeterName && i.Name.StartsWith("socalytics.analysis.partial_barriers", StringComparison.Ordinal))
				{
					l.EnableMeasurementEvents(i);
				}
			},
		};
		listener.SetMeasurementEventCallback<long>((i, v, t, _) =>
		{
			lock (sink)
			{
				sink.Add((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value)));
			}
		});
		listener.Start();
		return listener;
	}

	[Fact]
	public async Task A_failed_segment_with_the_minimum_met_publishes_the_match_job_after_the_last_segment_is_final()
	{
		var ct = TestContext.Current.CancellationToken;
		var measurements = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
		using var listener = Listen(measurements);
		await using var s = await StartAsync(CoveragePolicy.Partial(), 3, ct);

		await ApplyAsync(s, ct);
		await CompleteAsync(s, 1, ct);
		await CompleteAsync(s, 3, ct);
		await ApplyAsync(s, ct);
		(await JobStateAsync(s, "m", ct)).ShouldBe("Waiting");
		(await ReadyWorkAsync(s, "m", ct)).ShouldBe(0);
		(await InputsAsync(s, ct)).ShouldBeEmpty();

		await FailSegmentAsync(s, 2, ct);

		(await JobStateAsync(s, "m", ct)).ShouldBe("Ready");
		(await ReadyWorkAsync(s, "m", ct)).ShouldBe(1);
		var inputs = await InputsAsync(s, ct);
		var entry = JsonDocument.Parse(inputs).RootElement.EnumerateArray().Single();
		entry.GetProperty("upstreamNodeKey").GetString().ShouldBe("s");
		entry.GetProperty("status").GetString().ShouldBe("partial");
		entry.GetProperty("coverageMode").GetString().ShouldBe("partial");
		entry.GetProperty("acceptedResultIds").GetArrayLength().ShouldBe(2);
		entry.GetProperty("coverage").EnumerateArray().Select(c => c.GetProperty("state").GetString())
			.ShouldBe(["accepted", "failed", "accepted"]);

		lock (measurements)
		{
			measurements.ShouldContain(m => m.Name == "socalytics.analysis.partial_barriers_ready" && Equals(m.Tags["coverage"], "partial"));
		}

		var expected = await DigestAsync(s, ct);
		await s.Host.RestartAsync(ct);
		await ApplyAsync(s, ct);
		(await DigestAsync(s, ct)).ShouldBe(expected);
		(await ReconcileAsync(s, ct)).ShouldBe(expected);
		(await ReconcileAsync(s, ct)).ShouldBe(expected);

		// A retried match-scoped attempt keeps the input snapshot it started with.
		await ClaimAndExpireAsync(s, await JobAsync(s, "m", null, ct), ct);
		(await JobStateAsync(s, "m", ct)).ShouldBe("Ready");
		(await InputsAsync(s, ct)).ShouldBe(inputs);
		await ApplyAsync(s, ct);
		(await InputsAsync(s, ct)).ShouldBe(inputs);

		await CompleteJobAsync(s, await JobAsync(s, "m", null, ct), ct);
		await ApplyAsync(s, ct);
		await ApplyAsync(s, ct);
		(await RunStateAsync(s, ct)).ShouldBe("PartiallyCompleted");
	}

	[Fact]
	public async Task A_fully_accepted_partial_barrier_snapshots_complete_coverage_and_completes_the_run()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(CoveragePolicy.Partial(), 3, ct);
		await ApplyAsync(s, ct);
		for (var segment = 1; segment <= SegmentCount; segment++)
		{
			await CompleteAsync(s, segment, ct);
		}

		await ApplyAsync(s, ct);
		var entry = JsonDocument.Parse(await InputsAsync(s, ct)).RootElement.EnumerateArray().Single();
		entry.GetProperty("status").GetString().ShouldBe("accepted");
		entry.GetProperty("coverage").EnumerateArray().Select(c => c.GetProperty("state").GetString())
			.ShouldBe(["accepted", "accepted", "accepted"]);

		await CompleteJobAsync(s, await JobAsync(s, "m", null, ct), ct);
		await ApplyAsync(s, ct);
		await ApplyAsync(s, ct);
		(await RunStateAsync(s, ct)).ShouldBe("Completed");
	}

	[Fact]
	public async Task Coverage_below_the_minimum_blocks_the_match_job_without_publication()
	{
		var ct = TestContext.Current.CancellationToken;
		var measurements = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
		using var listener = Listen(measurements);
		await using var s = await StartAsync(CoveragePolicy.Partial(1m), 3, ct);

		await ApplyAsync(s, ct);
		await CompleteAsync(s, 1, ct);
		await CompleteAsync(s, 3, ct);
		await ApplyAsync(s, ct);
		(await JobStateAsync(s, "m", ct)).ShouldBe("Waiting");

		await FailSegmentAsync(s, 2, ct);

		(await JobStateAsync(s, "m", ct)).ShouldBe("Blocked");
		await using (var sql = await OpenAsync(s, ct))
		{
			(await sql.QuerySingleAsync<string>(
				JobsOfNode.Replace("SELECT j.id", "SELECT j.failure_reason").Replace("@key", "'m'"), new { s.RunId })).ShouldBe("upstream-coverage-below-minimum");
			(await sql.QuerySingleAsync<string>(
				"SELECT state FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId AND node_key = 'm'", new { s.RunId })).ShouldBe("Blocked");
		}

		(await ReadyWorkAsync(s, "m", ct)).ShouldBe(0);
		(await InputsAsync(s, ct)).ShouldBeEmpty();
		lock (measurements)
		{
			measurements.ShouldContain(m => m.Name == "socalytics.analysis.partial_barriers_blocked");
		}

		var expected = await DigestAsync(s, ct);
		await s.Host.RestartAsync(ct);
		await ApplyAsync(s, ct);
		(await DigestAsync(s, ct)).ShouldBe(expected);
		(await ReconcileAsync(s, ct)).ShouldBe(expected);
		(await ReadyWorkAsync(s, "m", ct)).ShouldBe(0);
		(await RunStateAsync(s, ct)).ShouldNotBe("Completed");
	}
}
