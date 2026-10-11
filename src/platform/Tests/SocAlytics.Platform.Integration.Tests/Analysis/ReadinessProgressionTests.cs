using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class ReadinessProgressionTests(PostgresContainerFixture postgres)
{
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
		await using var provider = services.BuildServiceProvider();

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
			new { runId })).ShouldBe(1);
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
}
