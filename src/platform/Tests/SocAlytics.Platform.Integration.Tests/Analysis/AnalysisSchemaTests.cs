using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class AnalysisSchemaTests(PostgresContainerFixture postgres)
{
	private static NewAnalysisRun NewRun(Guid setId, string workflowVersion, out Guid a, out Guid b)
	{
		a = Guid.CreateVersion7();
		b = Guid.CreateVersion7();
		var nodes = new[]
		{
			new WorkflowNodeRecord(b, "high", "cap.high", "1", CapabilityTier.HighLevel, ExecutionScope.Match, 1, WorkflowNodeState.Waiting, null, default, """{"n":"high"}"""),
			new WorkflowNodeRecord(a, "low", "cap.low", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, """{"n":"low"}"""),
		};
		var edges = new[] { new DependencyEdgeRecord(a, b, DependencyKind.Conditional, """{"kind":"capabilityRequested","capabilityId":"cap.low"}""") };
		return new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), setId, "wf", workflowVersion, ["cap.high", "cap.low"], "all-requested-capabilities",
			AnalysisRunState.Running, null, new AnalysisRunSnapshotRecord("""{"workflow":"wf"}""", "sha-256:" + new string('a', 64)), nodes, edges);
	}

	private async Task<(IsolatedDatabase Db, ServiceProvider Provider)> CreateAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString })
			.Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddInfrastructure();
		return (db, services.BuildServiceProvider());
	}

	private static async Task<Guid> InsertAsync(IServiceProvider root, NewAnalysisRun run, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var id = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
		await uow.CommitAsync(ct);
		return id;
	}

	[Fact]
	public async Task RunHierarchyRoundTripsAfterANewUnitOfWork()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var run = NewRun(Guid.NewGuid(), "1", out var low, out var high);
		var id = await InsertAsync(provider, run, ct);

		await using var scope = provider.CreateAsyncScope();
		var loaded = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LoadAsync(id, ct);
		loaded.ShouldNotBeNull();
		loaded.Run.Id.ShouldBe(id);
		loaded.Run.State.ShouldBe(AnalysisRunState.Running);
		loaded.Run.RunningAt.ShouldNotBeNull();
		loaded.Run.TerminatedAt.ShouldBeNull();
		loaded.Run.RequestedCapabilities.ShouldBe(["cap.high", "cap.low"]);
		loaded.Run.RecordingSetVersionId.ShouldBe(run.RecordingSetVersionId);
		loaded.Snapshot.SnapshotDigest.ShouldBe(run.Snapshot.SnapshotDigest);
		loaded.Nodes.Select(n => n.Id).ShouldBe([low, high]);
		loaded.Nodes.Select(n => n.Ordinal).ShouldBe([0, 1]);
		loaded.Nodes[0].Tier.ShouldBe(CapabilityTier.LowLevel);
		loaded.Nodes[0].ExecutionScope.ShouldBe(ExecutionScope.Segment);
		loaded.Nodes[0].State.ShouldBe(WorkflowNodeState.Active);
		loaded.Nodes[1].NodeKey.ShouldBe("high");
		loaded.Edges.Count.ShouldBe(1);
		loaded.Edges[0].UpstreamNodeId.ShouldBe(low);
		loaded.Edges[0].DependentNodeId.ShouldBe(high);
		loaded.Edges[0].Kind.ShouldBe(DependencyKind.Conditional);
		loaded.Edges[0].PredicateJson.ShouldNotBeNull();
	}

	[Fact]
	public async Task SecondRunForTheSameSetAndWorkflowVersionViolatesUniqueness()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var setId = Guid.NewGuid();
		await InsertAsync(provider, NewRun(setId, "1", out _, out _), ct);

		var ex = await Should.ThrowAsync<PostgresException>(() => InsertAsync(provider, NewRun(setId, "1", out _, out _), ct));
		ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		await InsertAsync(provider, NewRun(setId, "2", out _, out _), ct);
	}

	[Fact]
	public async Task StateGuardedTransitionAndLockWork()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var id = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out _), ct);

		await using var scope = provider.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var store = scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>();
		(await store.LockRunAsync(id, ct))!.State.ShouldBe(AnalysisRunState.Running);
		(await store.TryTransitionAsync(id, AnalysisRunState.Requested, AnalysisRunState.Failed, "x", ct)).ShouldBeFalse();
		(await store.TryTransitionAsync(id, AnalysisRunState.Running, AnalysisRunState.Completed, null, ct)).ShouldBeTrue();
		await uow.CommitAsync(ct);
	}

	[Theory]
	[InlineData("analysis_run_snapshots", "UPDATE", "SET snapshot_digest = 'x'")]
	[InlineData("analysis_run_snapshots", "DELETE", "")]
	[InlineData("analysis_node_snapshots", "UPDATE", "SET snapshot = '{}'::jsonb")]
	[InlineData("analysis_node_snapshots", "DELETE", "")]
	[InlineData("analysis_dependency_edges", "UPDATE", "SET kind = 'required', predicate = NULL")]
	[InlineData("analysis_dependency_edges", "DELETE", "")]
	public async Task ImmutableTablesRejectUpdateAndDeleteForTheRuntimeRole(string table, string verb, string set)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out _), ct);

		var sql = verb == "UPDATE" ? $"UPDATE socalytics.{table} {set}" : $"DELETE FROM socalytics.{table}";
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
	}
}
