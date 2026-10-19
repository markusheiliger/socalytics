using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class ReadinessRecoveryTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly string Digest = "sha-256:" + new string('a', 64);

	private sealed record Spec(string Key, bool Segment = false);

	private sealed record Link(string Up, string Down, string Kind = "required", string? Predicate = null);

	private static readonly Spec[] Nodes =
	[
		new("s1", true), new("s2", true), new("s3", true),
		new("m1"), new("m2"), new("m3"), new("m4"), new("m5"), new("m6"),
	];

	private static readonly Link[] Links =
	[
		new("s1", "m1"),
		new("s1", "m2"), new("s2", "m2", "optional"),
		new("s1", "m3"), new("s3", "m3", "conditional", "{\"kind\":\"nodeAccepted\",\"nodeKey\":\"s3\"}"),
		new("s3", "m4"),
		new("m4", "m5"),
		new("s2", "m6"),
	];

	private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var host = new AnalysisTestHost(db, nats, rustFs, workersEnabled: true);
		await host.StartAsync(ct);
		return (db, host);
	}

	private static async Task<Guid> SeedAsync(AnalysisTestHost host, CancellationToken ct)
	{
		var recordingVersion = Guid.NewGuid();
		var records = Nodes.Select((n, i) => new WorkflowNodeRecord(
			Guid.CreateVersion7(), n.Key, "cap-" + n.Key, "1.0.0", CapabilityTier.LowLevel, n.Segment ? ExecutionScope.Segment : ExecutionScope.Match, i,
			WorkflowNodeState.Waiting, null, default, "{\"analystProfileId\":\"profile\",\"policyVersions\":{}}")).ToList();
		var edges = Links.Select(l => new DependencyEdgeRecord(
			records.Single(n => n.NodeKey == l.Up).Id, records.Single(n => n.NodeKey == l.Down).Id,
			AnalysisPersistedNames.TryParseDependencyKind(l.Kind, out var k) ? k : throw new InvalidOperationException(), l.Predicate)).ToList();
		await using var scope = host.Services.CreateAsyncScope();
		var sp = scope.ServiceProvider;
		await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1.0.0", Nodes.Select(n => "cap-" + n.Key).ToList(), "all-requested-capabilities",
			AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"durationSeconds":300}}""", Digest), records, edges), ct);
		var jobs = new List<NewLogicalJob>();
		foreach (var record in records)
		{
			var segments = record.ExecutionScope == ExecutionScope.Segment ? new int?[] { 1, 2 } : [null];
			foreach (var number in segments)
			{
				jobs.Add(new NewLogicalJob(
					Guid.CreateVersion7(), runId, record.Id, record.ExecutionScope, number is null ? null : recordingVersion,
					number is null ? null : Digest, number is null ? null : Digest, number, LogicalJobState.Waiting,
					new LogicalJobSnapshotRecord(3, 60, 120, 600, Digest, Digest, null, "{\"cpu\":\"1\",\"memory\":\"1Gi\"}", "1.0.0")));
			}
		}

		await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(jobs, ct);
		await uow.CommitAsync(ct);
		return runId;
	}

	private static async Task ApplyAsync(AnalysisTestHost host, Guid runId, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
		await scope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
		await uow.CommitAsync(ct);
	}

	private static async Task<NpgsqlConnection> OpenAsync(IsolatedDatabase db, CancellationToken ct)
	{
		var c = new NpgsqlConnection(db.MigratorConnectionString);
		await c.OpenAsync(ct);
		return c;
	}

	private static async Task CompleteAsync(NpgsqlConnection sql, Guid runId, string key)
	{
		var ids = (await sql.QueryAsync<Guid>(
			"SELECT j.id FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.run_id = @runId AND n.node_key = @key",
			new { runId, key })).ToList();
		foreach (var id in ids)
		{
			await sql.ExecuteAsync(
				"""
				INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
				SELECT gen_random_uuid(), j.id, COALESCE((SELECT max(sequence) FROM socalytics.analysis_readiness_occurrences WHERE logical_job_id = j.id), 0) + 1, 'initial', now()
				FROM socalytics.analysis_logical_jobs j WHERE j.id = @id AND j.current_readiness_occurrence_id IS NULL;
				INSERT INTO socalytics.analysis_execution_attempts
					(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at, closed_at)
				SELECT @attemptId, j.id, COALESCE(j.current_readiness_occurrence_id, (SELECT id FROM socalytics.analysis_readiness_occurrences WHERE logical_job_id = j.id LIMIT 1)),
					1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Accepted', now(), now(), now(), now()
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
	}

	private static Task FailAsync(NpgsqlConnection sql, Guid runId, string key) => sql.ExecuteAsync(
		"""
		UPDATE socalytics.analysis_logical_jobs j SET state = 'Failed', failure_reason = 'attempts-exhausted'
		FROM socalytics.analysis_workflow_nodes n WHERE n.id = j.node_id AND j.run_id = @runId AND n.node_key = @key
		""",
		new { runId, key });

	private static async Task RecordOutcomesAsync(IsolatedDatabase db, Guid runId, CancellationToken ct)
	{
		await using var sql = await OpenAsync(db, ct);
		await CompleteAsync(sql, runId, "s1");
		await FailAsync(sql, runId, "s3");
	}

	private static async Task<string> SetsAsync(IsolatedDatabase db, Guid runId, CancellationToken ct)
	{
		await using var sql = await OpenAsync(db, ct);
		var rows = await sql.QueryAsync<string>(
			"""
			SELECT n.node_key || '/' || COALESCE(j.segment_number::text, '-') || '=' || j.state || '|' || COALESCE(i.inputs::text, '') AS row
			FROM socalytics.analysis_logical_jobs j
			JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
			LEFT JOIN socalytics.analysis_logical_job_inputs i ON i.logical_job_id = j.id
			WHERE j.run_id = @runId ORDER BY n.node_key, j.segment_number
			""",
			new { runId });
		var nodes = await sql.QueryAsync<string>(
			"SELECT node_key || '=' || state FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId ORDER BY node_key", new { runId });
		var run = await sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_runs WHERE id = @runId", new { runId });
		var text = string.Join('\n', rows.Concat(nodes).Append(run));
		return System.Text.RegularExpressions.Regex.Replace(text, "[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", "<id>");
	}

	private static async Task AssertNoDuplicatesAsync(IsolatedDatabase db, Guid runId, CancellationToken ct)
	{
		await using var sql = await OpenAsync(db, ct);
		(await sql.QuerySingleAsync<int>(
			"""
			SELECT count(*) FROM (SELECT o.logical_job_id, o.sequence FROM socalytics.analysis_readiness_occurrences o
			JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId
			GROUP BY 1, 2 HAVING count(*) > 1) d
			""", new { runId })).ShouldBe(0);
		(await sql.QuerySingleAsync<int>(
			"""
			SELECT count(*) FROM (SELECT causation_id FROM socalytics.outbox_messages
			WHERE run_id = @runId AND message_type = 'analysis.ready-work' GROUP BY causation_id HAVING count(*) > 1) d
			""", new { runId })).ShouldBe(0);
		(await sql.QuerySingleAsync<int>(
			"""
			SELECT count(*) FROM (SELECT logical_job_id FROM socalytics.outbox_messages
			WHERE run_id = @runId AND message_type = 'analysis.ready-work' AND state <> 'discarded' GROUP BY logical_job_id HAVING count(*) > 1) d
			""", new { runId })).ShouldBe(0);
	}

	[Fact]
	public async Task Readiness_after_restart_equals_an_uninterrupted_run_and_ignores_the_transport()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, host) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = host;
		var (refDb, refHost) = await StartAsync(ct);
		await using var ___ = refDb;
		await using var ____ = refHost;

		var runId = await SeedAsync(host, ct);
		var refRunId = await SeedAsync(refHost, ct);

		await ApplyAsync(host, runId, ct);
		await ApplyAsync(refHost, refRunId, ct);
		await RecordOutcomesAsync(db, runId, ct);
		await RecordOutcomesAsync(refDb, refRunId, ct);

		await host.RestartAsync(ct);
		await ApplyAsync(host, runId, ct);
		await ApplyAsync(refHost, refRunId, ct);

		var expected = await SetsAsync(refDb, refRunId, ct);
		var actual = await SetsAsync(db, runId, ct);
		actual.ShouldBe(expected);
		actual.ShouldContain("m1/-=Ready");
		actual.ShouldContain("m2/-=Ready");
		actual.ShouldContain("m3/-=Ready");
		actual.ShouldContain("m4/-=Blocked");
		actual.ShouldContain("m5/-=Blocked");
		actual.ShouldContain("m6/-=Waiting");
		await AssertNoDuplicatesAsync(db, runId, ct);

		await Task.Delay(TimeSpan.FromSeconds(5), ct);
		await using (var sql = await OpenAsync(db, ct))
		{
			var rows = (await sql.QueryAsync<(Guid Id, Guid JobId, string JobState)>(
				"""
				SELECT o.id, o.logical_job_id, j.state FROM socalytics.outbox_messages o
				JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id
				WHERE o.run_id = @runId AND o.message_type = 'analysis.ready-work'
				""", new { runId })).ToDictionary(r => r.Id.ToString());
			rows.Values.ShouldNotContain(r => r.JobState == "Blocked");
			var messages = await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct);
			foreach (var message in messages.Where(m => m.Subject.StartsWith("analysis.jobs.ready.", StringComparison.Ordinal) && m.MessageId is not null && rows.ContainsKey(m.MessageId)))
			{
				rows[message.MessageId!].JobState.ShouldNotBe("Blocked");
			}
		}

		await nats.ReplaceAsync();
		await host.RestartAsync(ct);
		await ApplyAsync(host, runId, ct);
		(await SetsAsync(db, runId, ct)).ShouldBe(expected);
		await AssertNoDuplicatesAsync(db, runId, ct);
	}
}
