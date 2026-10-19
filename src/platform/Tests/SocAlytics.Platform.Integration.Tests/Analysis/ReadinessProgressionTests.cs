using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Contracts;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Infrastructure.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class ReadinessProgressionTests(PostgresContainerFixture postgres)
{
	private sealed class TestEnvironment : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = "Test";

		public string ApplicationName { get; set; } = "SocAlytics.Platform.Api";

		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}

	private static string DevelopmentSettingsPath()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null)
		{
			var candidate = Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException("appsettings.Development.json not found.");
	}

	[Fact]
	public async Task Readiness_makes_root_segment_jobs_ready_once()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var configuration = new ConfigurationBuilder()
			.AddJsonFile(DevelopmentSettingsPath(), optional: false)
			.AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString })
			.Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddInfrastructure();
		services.AddApplication();
		services.AddLogging();
		services.AddScoped<IRequestContext, TestRequestContext>();
		services.AddHealthChecks();
		services.AddSingleton<IHostEnvironment>(new TestEnvironment());
		await using var provider = services.BuildServiceProvider();
		foreach (var seeder in provider.GetServices<IHostedService>().OfType<ConfiguredWorkflowSeeder>())
		{
			await seeder.StartAsync(ct);
			await seeder.StopAsync(ct);
		}

		var definition = ((WorkflowResolution.ResolvedResult)await provider.GetRequiredService<IWorkflowDefinitionResolver>()
			.ResolveDefaultAsync(ct)).Definition;
		var digestText = definition.SegmentationPolicy.Digest.ToString();
		var lineage = new RecordingSetLineage(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch,
			[new RecordingSetLineageMember(1, Guid.NewGuid(), digestText, Guid.NewGuid(), digestText, [new TimelineSpan(0, 900_000, 0)])]);

		Guid runId;
		await using (var scope = provider.CreateAsyncScope())
		{
			var sp = scope.ServiceProvider;
			var mapped = sp.GetRequiredService<WorkflowSnapshotMapper>().Map(definition);
			var segments = sp.GetRequiredService<IAnalysisSegmentSource>()
				.GetSegments(lineage, definition.SegmentationPolicy.DurationSeconds, definition.SegmentationPolicy.Digest);
			segments.Count.ShouldBe(3);
			var planned = LogicalJobPlanner.Plan(mapped.PlannerNodes, segments);

			await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			var nodes = definition.Nodes.Select((n, i) => new WorkflowNodeRecord(
				Guid.CreateVersion7(), n.NodeKey, n.CapabilityId, n.CapabilityVersion, n.Tier, n.ExecutionScope, i,
				WorkflowNodeState.Waiting, null, default, mapped.NodeSnapshotJson[n.NodeKey])).ToList();
			var edges = definition.Edges.Select(e => new DependencyEdgeRecord(
				nodes.Single(n => n.NodeKey == e.UpstreamNodeKey).Id,
				nodes.Single(n => n.NodeKey == e.DependentNodeKey).Id,
				AnalysisPersistedNames.TryParseDependencyKind(e.Kind, out var k) ? k : throw new InvalidOperationException(),
				null)).ToList();
			var runs = sp.GetRequiredService<IAnalysisRunStore>();
			runId = await runs.InsertAsync(new NewAnalysisRun(
				lineage.TeamId, lineage.MatchId, lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion,
				definition.RequestedCapabilities, definition.CompletionPolicy, AnalysisRunState.Requested, null,
				new AnalysisRunSnapshotRecord(mapped.RunSnapshotJson, mapped.RunSnapshotDigest), nodes, edges), ct);
			await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(planned.Select(p => new NewLogicalJob(
				Guid.CreateVersion7(), runId, nodes.Single(n => n.NodeKey == p.NodeKey).Id, p.Scope,
				p.Segment?.RecordingVersionId, p.Segment?.TimelineMappingDigest.ToString(),
				p.Segment?.SegmentationPolicyDigest.ToString(), p.Segment?.SegmentNumber, LogicalJobState.Waiting,
				new LogicalJobSnapshotRecord(
					p.Snapshot.MaxAttempts, p.Snapshot.LeaseSeconds, p.Snapshot.StaleTimeoutSeconds, p.Snapshot.ExecutionTimeoutSeconds,
					p.Snapshot.AnalystProfileDigest.ToString(), p.Snapshot.ImageDigest.ToString(), p.Snapshot.ModelDigest?.ToString(),
					System.Text.Json.JsonSerializer.Serialize(p.Snapshot.Resources), p.Snapshot.SchemaVersion))).ToList(), ct);

			await runs.LockRunAsync(runId, ct);
			var coordinator = sp.GetRequiredService<RunReadinessCoordinator>();
			var first = await coordinator.ApplyAsync(runId, ct);
			first.ReadyJobs.ShouldBe(3);
			first.RunTransitions.ShouldBe(1);
			var second = await coordinator.ApplyAsync(runId, ct);
			second.ReadyJobs.ShouldBe(0);
			second.RunTransitions.ShouldBe(0);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.run_id = @runId AND n.execution_scope = 'segment' AND j.state = 'Ready' AND j.current_readiness_occurrence_id IS NOT NULL",
			new { runId })).ShouldBe(3);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.run_id = @runId AND n.execution_scope = 'match' AND j.state = 'Waiting'",
			new { runId })).ShouldBe(2);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.analysis_logical_job_inputs i JOIN socalytics.analysis_logical_jobs j ON j.id = i.logical_job_id WHERE j.run_id = @runId",
			new { runId })).ShouldBe(3);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.analysis_readiness_occurrences o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId AND o.sequence = 1 AND o.cause = 'initial'",
			new { runId })).ShouldBe(3);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work' AND run_id = @runId",
			new { runId })).ShouldBe(3);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = @runId",
			new { runId })).ShouldBe(1);
		(await connection.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.analysis_runs WHERE id = @runId AND state = 'Running' AND running_at IS NOT NULL AND created_at IS NOT NULL",
			new { runId })).ShouldBe(1);
	}

	private sealed record Spec(string Key, bool Segment = false);

	private sealed record Link(string Up, string Down, string Kind = "required", string? Predicate = null);

	private const string DefaultRunSnapshot = """{"segmentationPolicy":{"durationSeconds":300}}""";

	private static readonly string Digest = "sha-256:" + new string('a', 64);

	private sealed class Fixture(ServiceProvider provider, NpgsqlConnection sql, Guid runId) : IAsyncDisposable
	{
		public async Task<ReadinessApplication> ApplyAsync(CancellationToken ct)
		{
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
			var result = await scope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
			await uow.CommitAsync(ct);
			return result;
		}

		private const string JobIds =
			"SELECT j.id FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE j.run_id = @runId AND n.node_key = @key";

		public async Task<List<Guid>> JobsAsync(string key) => (await sql.QueryAsync<Guid>(JobIds + " ORDER BY j.segment_number", new { runId, key })).ToList();

		public async Task<string> JobStateAsync(string key) =>
			await sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_logical_jobs WHERE id = @id", new { id = (await JobsAsync(key))[0] });

		public Task<string> NodeStateAsync(string key) =>
			sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId AND node_key = @key", new { runId, key });

		public Task<string> RunStateAsync() => sql.QuerySingleAsync<string>("SELECT state FROM socalytics.analysis_runs WHERE id = @runId", new { runId });

		public Task<int> OutboxAsync(string type, string? state = null) => sql.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.outbox_messages WHERE run_id = @runId AND message_type = @type AND (@state::text IS NULL OR state = @state)", new { runId, type, state });

		public async Task<int> ReadyWorkAsync(string key) => await sql.QuerySingleAsync<int>(
			"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work' AND logical_job_id IN (" + JobIds + ")", new { runId, key });

		public async Task<string?> InputsAsync(string key) => await sql.QuerySingleOrDefaultAsync<string>(
			"SELECT inputs::text FROM socalytics.analysis_logical_job_inputs WHERE logical_job_id = @id", new { id = (await JobsAsync(key))[0] });

		public async Task CompleteAsync(string key, int? index = null)
		{
			var all = await JobsAsync(key);
			var targets = index is { } i ? [all[i]] : all;
			foreach (var id in targets)
			{
				var attemptId = Guid.NewGuid();
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
					new { id, attemptId, digest = Digest });
			}
		}

		public async Task<List<(Guid RecordingVersionId, int? SegmentNumber, JsonElement Payload)>> PublishedJobsAsync(string key)
		{
			var rows = await sql.QueryAsync<(Guid? RecordingVersionId, int? SegmentNumber, string Payload)>(
				"SELECT j.recording_version_id, j.segment_number, o.payload::text FROM socalytics.outbox_messages o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE o.message_type = 'analysis.ready-work' AND o.logical_job_id IN (" + JobIds + ") ORDER BY j.segment_number",
				new { runId, key });
			return rows.Select(r => (r.RecordingVersionId ?? Guid.Empty, r.SegmentNumber, JsonDocument.Parse(r.Payload).RootElement.Clone())).ToList();
		}

		public async Task FailAsync(string key) =>
			await sql.ExecuteAsync("UPDATE socalytics.analysis_logical_jobs SET state = 'Failed', failure_reason = 'attempts-exhausted' WHERE id = ANY(@ids)", new { ids = (await JobsAsync(key)).ToArray() });

		public async Task ForceReadyWithPendingRowAsync(string key)
		{
			var id = (await JobsAsync(key))[0];
			await sql.ExecuteAsync("UPDATE socalytics.analysis_logical_jobs SET state = 'Ready' WHERE id = @id", new { id });
			await sql.ExecuteAsync(
				"INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, run_id, logical_job_id, state, created_at) VALUES (gen_random_uuid(), 'analysis.ready-work', '1.0.0', 's', '{}'::jsonb, gen_random_uuid(), @runId, @id, 'pending', now())",
				new { runId, id });
		}

		public async ValueTask DisposeAsync()
		{
			await sql.DisposeAsync();
			await provider.DisposeAsync();
		}
	}

	private async Task<Fixture> CreateAsync(IsolatedDatabase db, Spec[] nodes, Link[] links, CancellationToken ct, string runSnapshot = DefaultRunSnapshot)
	{
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var configuration = new ConfigurationBuilder()
			.AddJsonFile(DevelopmentSettingsPath(), optional: false)
			.AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString })
			.Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddTestHostEnvironment();
		services.AddInfrastructure();
		services.AddApplication();
		var provider = services.BuildServiceProvider();

		var recordingVersion = Guid.NewGuid();
		var records = nodes.Select((n, i) => new WorkflowNodeRecord(
			Guid.CreateVersion7(), n.Key, "cap-" + n.Key, "1.0.0", CapabilityTier.LowLevel, n.Segment ? ExecutionScope.Segment : ExecutionScope.Match, i,
			WorkflowNodeState.Waiting, null, default, "{\"analystProfileId\":\"profile\",\"policyVersions\":{}}")).ToList();
		var edges = links.Select(l => new DependencyEdgeRecord(
			records.Single(n => n.NodeKey == l.Up).Id, records.Single(n => n.NodeKey == l.Down).Id,
			AnalysisPersistedNames.TryParseDependencyKind(l.Kind, out var k) ? k : throw new InvalidOperationException(), l.Predicate)).ToList();
		await using var scope = provider.CreateAsyncScope();
		var sp = scope.ServiceProvider;
		await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1.0.0", nodes.Select(n => "cap-" + n.Key).ToList(), "all-requested-capabilities",
			AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord(runSnapshot, Digest), records, edges), ct);
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
		var sql = new NpgsqlConnection(db.MigratorConnectionString);
		await sql.OpenAsync(ct);
		return new Fixture(provider, sql, runId);
	}

	private static string Accepted(string key) => $"{{\"kind\":\"nodeAccepted\",\"nodeKey\":\"{key}\"}}";

	[Fact]
	public async Task Required_dependency_without_results_keeps_dependent_waiting()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("a"), new("b")], [new("a", "b")], ct);
		(await f.ApplyAsync(ct)).ReadyJobs.ShouldBe(1);
		(await f.JobStateAsync("b")).ShouldBe("Waiting");
		(await f.ReadyWorkAsync("b")).ShouldBe(0);
		(await f.ReadyWorkAsync("a")).ShouldBe(1);
	}

	[Fact]
	public async Task Optional_inputs_are_snapshotted_absent_or_failed_and_late_results_do_not_change_them()
	{
		var ct = TestContext.Current.CancellationToken;
		var nodes = new Spec[] { new("a"), new("c"), new("b") };
		var links = new Link[] { new("a", "b", "optional"), new("c", "b") };
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, nodes, links, ct);
		await f.ApplyAsync(ct);
		await f.CompleteAsync("c");
		await f.ApplyAsync(ct);
		(await f.JobStateAsync("b")).ShouldBe("Ready");
		var snapshot = await f.InputsAsync("b");
		snapshot.ShouldNotBeNull().ShouldContain("absent");
		await f.CompleteAsync("a");
		await f.ApplyAsync(ct);
		(await f.InputsAsync("b")).ShouldBe(snapshot);
		(await f.ReadyWorkAsync("b")).ShouldBe(1);

		await using var db2 = await postgres.CreateDatabaseAsync(ct);
		await using var g = await CreateAsync(db2, nodes, links, ct);
		await g.ApplyAsync(ct);
		await g.FailAsync("a");
		await g.CompleteAsync("c");
		await g.ApplyAsync(ct);
		(await g.JobStateAsync("b")).ShouldBe("Ready");
		(await g.InputsAsync("b")).ShouldNotBeNull().ShouldContain("failed");
	}

	[Theory]
	[InlineData("fail", "Ready", "not-applicable")]
	[InlineData("complete", "Ready", "accepted")]
	[InlineData("none", "Waiting", null)]
	public async Task Conditional_edges_follow_their_predicate(string outcome, string expectedState, string? status)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("a"), new("c"), new("b")], [new("a", "b", "conditional", Accepted("a")), new("c", "b")], ct);
		await f.ApplyAsync(ct);
		if (outcome == "fail") { await f.FailAsync("a"); }
		if (outcome == "complete") { await f.CompleteAsync("a"); }
		await f.CompleteAsync("c");
		await f.ApplyAsync(ct);
		(await f.JobStateAsync("b")).ShouldBe(expectedState);
		if (status is not null)
		{
			(await f.InputsAsync("b")).ShouldNotBeNull().ShouldContain(status);
		}
	}

	[Fact]
	public async Task Match_job_waits_for_every_segment()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("s", true), new("m")], [new("s", "m")], ct);
		(await f.ApplyAsync(ct)).ReadyJobs.ShouldBe(2);
		await f.CompleteAsync("s", 0);
		await f.ApplyAsync(ct);
		(await f.JobStateAsync("m")).ShouldBe("Waiting");
		await f.CompleteAsync("s", 1);
		await f.ApplyAsync(ct);
		(await f.JobStateAsync("m")).ShouldBe("Ready");
		(await f.ReadyWorkAsync("m")).ShouldBe(1);
	}

	[Fact]
	public async Task Published_jobs_carry_the_workflow_and_segment_jobs_the_shared_segment_window()
	{
		var ct = TestContext.Current.CancellationToken;
		const string snapshot = """{"segmentationPolicy":{"durationSeconds":300}}""";
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("s", true), new("m")], [new("s", "m")], ct, snapshot);
		await f.ApplyAsync(ct);
		await f.CompleteAsync("s");
		await f.ApplyAsync(ct);

		var validator = new JsonSchemaContractValidator();
		var durationMs = SegmentWindows.DurationMs(snapshot);
		var segments = await f.PublishedJobsAsync("s");
		segments.Count.ShouldBe(2);
		foreach (var (recordingVersionId, segmentNumber, payload) in segments)
		{
			validator.Validate(ContractSchemaIds.AnalystJobV1, payload).IsValid.ShouldBeTrue();
			payload.GetProperty("contractVersion").GetString().ShouldBe("1.1.0");
			payload.GetProperty("workflow").GetProperty("id").GetString().ShouldBe("wf");
			payload.GetProperty("workflow").GetProperty("version").GetString().ShouldBe("1.0.0");
			var window = payload.GetProperty("segmentWindow");
			var (startMs, endMs) = SegmentWindows.Window(segmentNumber!.Value, durationMs);
			window.GetProperty("segmentId").GetString().ShouldBe(SegmentWindows.SegmentId(recordingVersionId, segmentNumber.Value));
			window.GetProperty("startMs").GetInt64().ShouldBe(startMs);
			window.GetProperty("endMs").GetInt64().ShouldBe(endMs);
			endMs.ShouldBe(segmentNumber.Value * 300_000L);
		}

		var matches = await f.PublishedJobsAsync("m");
		matches.Count.ShouldBe(1);
		var match = matches[0].Payload;
		validator.Validate(ContractSchemaIds.AnalystJobV1, match).IsValid.ShouldBeTrue();
		match.GetProperty("contractVersion").GetString().ShouldBe("1.1.0");
		match.GetProperty("workflow").GetProperty("id").GetString().ShouldBe("wf");
		match.GetProperty("workflow").GetProperty("version").GetString().ShouldBe("1.0.0");
		match.TryGetProperty("segmentWindow", out _).ShouldBeFalse();
	}

	[Fact]
	public async Task Failed_required_upstream_blocks_only_descendants()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("a"), new("b"), new("c"), new("d")], [new("a", "b"), new("b", "c")], ct);
		await f.ApplyAsync(ct);
		await f.ForceReadyWithPendingRowAsync("b");
		await f.CompleteAsync("d");
		await f.FailAsync("a");
		await f.ApplyAsync(ct);
		(await f.NodeStateAsync("a")).ShouldBe("Failed");
		(await f.NodeStateAsync("b")).ShouldBe("Blocked");
		(await f.NodeStateAsync("c")).ShouldBe("Blocked");
		(await f.NodeStateAsync("d")).ShouldBe("Completed");
		(await f.JobStateAsync("b")).ShouldBe("Blocked");
		(await f.JobStateAsync("c")).ShouldBe("Blocked");
		(await f.JobStateAsync("d")).ShouldBe("Completed");
		(await f.OutboxAsync("analysis.ready-work", "discarded")).ShouldBe(1);
		(await f.ReadyWorkAsync("c")).ShouldBe(0);
		(await f.RunStateAsync()).ShouldBe("PartiallyCompleted");
		var again = await f.ApplyAsync(ct);
		again.ReadyJobs.ShouldBe(0);
		again.RunTransitions.ShouldBe(0);
	}

	[Theory]
	[InlineData("Completed", false)]
	[InlineData("Failed", true)]
	public async Task Single_node_runs_reach_their_terminal_state(string expected, bool fail)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		await using var f = await CreateAsync(db, [new("a")], [], ct);
		await f.ApplyAsync(ct);
		if (fail) { await f.FailAsync("a"); } else { await f.CompleteAsync("a"); }
		await f.ApplyAsync(ct);
		(await f.RunStateAsync()).ShouldBe(expected);
		(await f.OutboxAsync("analysis.run-state-changed")).ShouldBe(2);
		await f.ApplyAsync(ct);
		(await f.OutboxAsync("analysis.run-state-changed")).ShouldBe(2);
	}
}
