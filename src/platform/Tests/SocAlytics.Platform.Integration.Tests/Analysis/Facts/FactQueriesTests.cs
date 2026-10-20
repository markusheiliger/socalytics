using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Facts;

public sealed class FactQueriesTests(PostgresContainerFixture postgres)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record SeededRun(Guid RunId, Guid DetectNode, Guid TwinNode, Guid UnusedNode, Guid[] ResultIds, Guid[] JobIds);

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

	private static string NodeSnapshot(string key, string capability, string schema) =>
		$$"""
		{"nodeKey":"{{key}}","capabilityId":"{{capability}}","schemaVersion":"{{schema}}","analystProfileId":"profile-{{key}}",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":null}
		""";

	// Two segment nodes sharing a capability, one match node without jobs; the detect node has two segment jobs with accepted results.
	private static async Task<SeededRun> SeedAsync(IsolatedDatabase db, ServiceProvider provider, CancellationToken ct)
	{
		var detect = Guid.CreateVersion7();
		var twin = Guid.CreateVersion7();
		var unused = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[
				new WorkflowNodeRecord(detect, "detect", "cap.detect", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, NodeSnapshot("detect", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(twin, "twin", "cap.detect", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 1, WorkflowNodeState.Waiting, null, default, NodeSnapshot("twin", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(unused, "unused", "cap.other", "2", CapabilityTier.HighLevel, ExecutionScope.Match, 2, WorkflowNodeState.Waiting, null, default, NodeSnapshot("unused", "cap.other", "2.0.0")),
			],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var jobs = new[] { 1, 2, 3 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, detect, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();

		Guid runId;
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([.. jobs.Select(j => j with { RunId = runId })], ct);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var results = new List<Guid>();
		foreach (var job in jobs.Take(2))
		{
			var occurrence = Guid.NewGuid();
			var attempt = Guid.NewGuid();
			await connection.ExecuteAsync(new CommandDefinition(
				"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occurrence, @jobId, 1, 'initial', now())",
				new { occurrence, jobId = job.Id }, cancellationToken: ct));
			await connection.ExecuteAsync(new CommandDefinition(
				"""
				INSERT INTO socalytics.analysis_execution_attempts
					(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
				VALUES (@attempt, @jobId, @occurrence, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Stale', now(), now(), now())
				""",
				new { attempt, jobId = job.Id, occurrence }, cancellationToken: ct));
			var result = new AcceptedResultRecord(
				Guid.NewGuid(), job.Id, runId, detect, attempt, "results/key", DigestA, "cap.detect", "1", "1.0.0",
				DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await uow.CommitAsync(ct);
			results.Add(result.Id);
		}

		await connection.ExecuteAsync(new CommandDefinition(
			"UPDATE socalytics.analysis_logical_jobs SET state = 'Failed', failure_reason = 'attempts-exhausted' WHERE id = @id",
			new { id = jobs[2].Id }, cancellationToken: ct));

		return new SeededRun(runId, detect, twin, unused, [.. results], [.. jobs.Select(j => j.Id)]);
	}

	private static async Task InsertFactsAsync(IServiceProvider root, SeededRun seed, Guid resultId, params (int Ordinal, long Start, long End)[] facts)
	{
		var ct = TestContext.Current.CancellationToken;
		var records = facts.Select(f => new IndexedFactRecord(
			resultId, f.Ordinal, seed.RunId, seed.DetectNode, Guid.NewGuid(), Guid.NewGuid(), "cap.detect", "1", "1.0.0",
			"foundational-facts", "pass", f.Start, f.End, 0.5, $$"""{"o":{{f.Ordinal}}}""")).ToList();
		await using var scope = root.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(records, ct);
		await uow.CommitAsync(ct);
	}

	private static async Task<T> WithQueriesAsync<T>(IServiceProvider root, Func<IFactQueries, Task<T>> action)
	{
		await using var scope = root.CreateAsyncScope();
		return await action(scope.ServiceProvider.GetRequiredService<IFactQueries>());
	}

	[Fact]
	public async Task PagesInOrderAcrossTiesWithoutDuplicatesOrGaps()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);

		// Identical ranges in two results and several ordinals force ties on (start, end).
		await InsertFactsAsync(provider, seed, seed.ResultIds[0], (0, 0, 100), (1, 0, 100), (2, 50, 150));
		await InsertFactsAsync(provider, seed, seed.ResultIds[1], (0, 0, 100), (1, 300, 400), (2, 0, 50));

		var all = new List<FactItemRecord>();
		FactPageKey? after = null;
		var pages = 0;
		while (true)
		{
			var page = await WithQueriesAsync(provider, q => q.QueryPageAsync(new FactPageQuery(seed.RunId, seed.DetectNode, null, null, after, 2), ct));
			pages++;
			all.AddRange(page.Items);
			if (!page.HasMore)
			{
				break;
			}

			var last = page.Items[^1];
			after = new FactPageKey(last.StartMs, last.EndMs, last.AcceptedResultId, last.Ordinal);
		}

		pages.ShouldBe(3);
		all.Count.ShouldBe(6);
		all.Select(f => (f.AcceptedResultId, f.Ordinal)).Distinct().Count().ShouldBe(6);
		all.Select(f => (f.StartMs, f.EndMs, f.AcceptedResultId, f.Ordinal))
			.ShouldBe(all.Select(f => (f.StartMs, f.EndMs, f.AcceptedResultId, f.Ordinal))
				.OrderBy(k => k.StartMs).ThenBy(k => k.EndMs).ThenBy(k => k.AcceptedResultId).ThenBy(k => k.Ordinal));
		all[0].PayloadJson.ShouldContain("\"o\"");
	}

	[Theory]
	[InlineData(null, null, 3)]
	[InlineData(100L, null, 2)]
	[InlineData(150L, null, 1)]
	[InlineData(null, 100L, 2)]
	[InlineData(null, 50L, 1)]
	[InlineData(100L, 150L, 2)]
	[InlineData(150L, 200L, 1)]
	[InlineData(200L, 300L, 0)]
	public async Task SelectsFactsOverlappingTheHalfOpenRange(long? from, long? to, int expected)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);
		await InsertFactsAsync(provider, seed, seed.ResultIds[0], (0, 0, 100), (1, 50, 150), (2, 100, 200));
		// [0,100) ends at 100, so from=100 excludes it; [100,200) starts at 100, so to=100 excludes it.

		var page = await WithQueriesAsync(provider, q => q.QueryPageAsync(new FactPageQuery(seed.RunId, seed.DetectNode, from, to, null, 10), ct));

		page.Items.Count.ShouldBe(expected);
	}

	[Fact]
	public async Task ResolvesNodesByCapabilityVersionSchemaAndKeyAndReportsAmbiguity()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);

		var both = await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.detect", "1", "1.0.0", null, ct));
		both.Select(n => n.NodeKey).ShouldBe(["detect", "twin"]);
		both[0].OutputType.ShouldBe("foundational-facts");
		both[0].ExecutionScope.ShouldBe("segment");
		both[0].AnalystProfileId.ShouldBe("profile-detect");
		both[0].Required.ShouldBeTrue();
		both[1].Required.ShouldBeFalse();

		(await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.detect", "1", "1.0.0", "twin", ct)))
			.Select(n => n.NodeKey).ShouldBe(["twin"]);
		(await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.detect", "1", "2.0.0", null, ct))).ShouldBeEmpty();
		(await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.detect", "2", "1.0.0", null, ct))).ShouldBeEmpty();
		(await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.detect", "1", "1.0.0", "missing", ct))).ShouldBeEmpty();
		(await WithQueriesAsync(provider, q => q.ResolveNodesAsync(Guid.NewGuid(), "cap.detect", "1", "1.0.0", null, ct))).ShouldBeEmpty();

		var unused = await WithQueriesAsync(provider, q => q.ResolveNodesAsync(seed.RunId, "cap.other", "2", "2.0.0", null, ct));
		unused.Single().OutputType.ShouldBe("soccer-concepts");
		unused.Single().Required.ShouldBeFalse();
		(await WithQueriesAsync(provider, q => q.ListNodesAsync(seed.RunId, ct))).Count.ShouldBe(3);
	}

	[Fact]
	public async Task IsolatesRunsAndCountsFactsPerNode()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var first = await SeedAsync(db, provider, ct);
		var second = await SeedAsync(db, provider, ct);
		await InsertFactsAsync(provider, first, first.ResultIds[0], (0, 0, 10), (1, 10, 20));
		await InsertFactsAsync(provider, second, second.ResultIds[0], (0, 0, 10));

		(await WithQueriesAsync(provider, q => q.QueryPageAsync(new FactPageQuery(first.RunId, first.DetectNode, null, null, null, 10), ct)))
			.Items.Count.ShouldBe(2);
		(await WithQueriesAsync(provider, q => q.QueryPageAsync(new FactPageQuery(first.RunId, second.DetectNode, null, null, null, 10), ct)))
			.Items.ShouldBeEmpty();

		var counts = await WithQueriesAsync(provider, q => q.CountFactsByNodeAsync(first.RunId, ct));
		counts.Count.ShouldBe(1);
		counts[first.DetectNode].ShouldBe(2);
	}

	[Fact]
	public async Task ReturnsRunProvenanceCoverageInputsAndAChangingVersionBasis()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);

		var run = await WithQueriesAsync(provider, q => q.GetRunAsync(seed.RunId, ct));
		run.ShouldNotBeNull();
		run.WorkflowId.ShouldBe("wf");
		run.WorkflowVersion.ShouldBe("1");
		run.SegmentDurationMs.ShouldBe(300_000);
		run.RecordingCoverageEndMs.ShouldBeNull();
		(await WithQueriesAsync(provider, q => q.GetRunAsync(Guid.NewGuid(), ct))).ShouldBeNull();

		var inputs = await WithQueriesAsync(provider, q => q.GetCoverageInputsAsync(seed.RunId, seed.DetectNode, ct));
		inputs.SegmentDurationMs.ShouldBe(300_000);
		inputs.Jobs.Select(j => j.SegmentNumber).ShouldBe([1, 2, 3]);
		inputs.Jobs.Select(j => j.HasAcceptedResult).ShouldBe([true, true, false]);
		inputs.Jobs[2].State.ShouldBe("Failed");
		(await WithQueriesAsync(provider, q => q.GetCoverageInputsAsync(seed.RunId, seed.TwinNode, ct))).Jobs.ShouldBeEmpty();

		await InsertFactsAsync(provider, seed, seed.ResultIds[0], (0, 0, 10));
		var after = await WithQueriesAsync(provider, q => q.GetRunAsync(seed.RunId, ct));
		after!.VersionBasis.ShouldNotBe(run.VersionBasis);
	}
}
