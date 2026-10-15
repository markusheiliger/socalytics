using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class UpstreamResultReaderTests(PostgresContainerFixture postgres)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);
	private const long SegmentMs = 300_000;

	private sealed record Seed(Guid DependentJobId, Guid RecordingVersionId, Guid[] SnapshotResults, Guid LateResult);

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

	// Upstream segment node "low" with five segments, and a match node "high" whose job snapshot names the upstream.
	// Segments 1 and 2 are in the snapshot, 3 is blocked, 4 completed after the snapshot, and 5 failed.
	private static async Task<Seed> SeedAsync(IsolatedDatabase db, ServiceProvider provider, string snapshotStatus, CancellationToken ct)
	{
		var low = Guid.CreateVersion7();
		var high = Guid.CreateVersion7();
		var recording = Guid.NewGuid();
		var run = new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1", ["cap.high"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"durationSeconds":300}}""", DigestA),
			[
				new WorkflowNodeRecord(low, "low", "cap.low", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, """{"n":"low"}"""),
				new WorkflowNodeRecord(high, "high", "cap.high", "1", CapabilityTier.HighLevel, ExecutionScope.Match, 1, WorkflowNodeState.Waiting, null, default, """{"n":"high"}"""),
			],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1");
		var states = new[] { LogicalJobState.Completed, LogicalJobState.Completed, LogicalJobState.Blocked, LogicalJobState.Completed, LogicalJobState.Failed };
		var lowJobs = states.Select((s, i) => new NewLogicalJob(
			Guid.CreateVersion7(), default, low, ExecutionScope.Segment, recording, DigestA, DigestB, i + 1, s, snapshot)).ToList();
		var highJob = new NewLogicalJob(Guid.CreateVersion7(), default, high, ExecutionScope.Match, null, null, null, null, LogicalJobState.Ready, snapshot);

		Guid runId;
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync(
				[.. lowJobs.Select(j => j with { RunId = runId }), highJob with { RunId = runId }], ct);
			await uow.CommitAsync(ct);
		}

		var resultIds = new Dictionary<int, Guid>();
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		foreach (var segment in new[] { 1, 2, 4 })
		{
			var job = lowJobs[segment - 1];
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
				Guid.NewGuid(), job.Id, runId, low, attempt, $"results/{segment}", DigestA, "cap.low", "1", "1",
				DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
			var manifest = new AcceptedResultManifestRecord(
				result.Id, "1.0.0", "foundational-facts", (segment - 1) * SegmentMs, segment * SegmentMs, segment, "[]", "[]");
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().InsertAsync(manifest, ct);
			await uow.CommitAsync(ct);
			resultIds[segment] = result.Id;
		}

		var inSnapshot = snapshotStatus == SnapshotStatus.Accepted ? new[] { resultIds[2], resultIds[1] } : [];
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			var json = JsonSerializer.Serialize(new[]
			{
				new { upstreamNodeKey = "low", edgeKind = "required", status = snapshotStatus, acceptedResultIds = inSnapshot },
			});
			(await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().TryInsertInputsAsync(highJob.Id, json, ct)).ShouldBeTrue();
			await uow.CommitAsync(ct);
		}

		return new Seed(highJob.Id, recording, [.. inSnapshot.OrderBy(i => i)], resultIds[4]);
	}

	private static async Task<UpstreamResultsPage> ReadAsync(
		IServiceProvider root, Seed seed, int pageSize = 100, string? token = null,
		IReadOnlyList<string>? segmentIds = null, long? start = null, long? end = null, string node = "low")
	{
		await using var scope = root.CreateAsyncScope();
		var page = await scope.ServiceProvider.GetRequiredService<IUpstreamResultReader>().ReadAsync(
			new UpstreamReadRequest(seed.DependentJobId, node, segmentIds, start, end, pageSize, token), TestContext.Current.CancellationToken);
		return page.ShouldNotBeNull();
	}

	[Fact]
	public async Task ReturnsExactlyTheSnapshotResultsAndReportsALateResultAsMissing()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, SnapshotStatus.Accepted, ct);

		var page = await ReadAsync(provider, seed);

		page.Results.Select(r => r.AcceptedResultId).ShouldBe(
			page.Results.OrderBy(r => r.WindowStartMs).Select(r => r.AcceptedResultId));
		page.Results.Select(r => r.AcceptedResultId).Order().ShouldBe(seed.SnapshotResults);
		page.Results.ShouldNotContain(r => r.AcceptedResultId == seed.LateResult);
		page.Results.Select(r => r.WindowStartMs).ShouldBe([0L, SegmentMs]);
		page.Results.ShouldAllBe(r => r.WindowEndMs - r.WindowStartMs == SegmentMs && r.SegmentId != null);
		page.Results[0].Manifest.FactCount.ShouldBe(1);
		page.ContinuationToken.ShouldBeNull();
		page.Coverage.Select(c => c.State).ShouldBe(
			[CoverageState.Accepted, CoverageState.Accepted, CoverageState.Blocked, CoverageState.Missing, CoverageState.Failed]);
		page.Coverage.Select(c => c.SegmentId).ShouldBe(Enumerable.Range(1, 5).Select(n => $"{seed.RecordingVersionId}-{n}"));
	}

	[Fact]
	public async Task NotApplicableUpstreamHasNoResultsAndSkippedCoverage()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, SnapshotStatus.NotApplicable, ct);

		var page = await ReadAsync(provider, seed);

		page.Results.ShouldBeEmpty();
		page.Coverage.Select(c => c.State).ShouldBe(
			[CoverageState.Skipped, CoverageState.Skipped, CoverageState.Skipped, CoverageState.Skipped, CoverageState.Skipped]);
	}

	[Fact]
	public async Task UnknownUpstreamNodeIsNotFoundAndInvalidTokenIsRejected()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, SnapshotStatus.Accepted, ct);

		await using var scope = provider.CreateAsyncScope();
		var reader = scope.ServiceProvider.GetRequiredService<IUpstreamResultReader>();
		(await reader.ReadAsync(new UpstreamReadRequest(seed.DependentJobId, "other", null, null, null, 10, null), ct)).ShouldBeNull();
		await Should.ThrowAsync<InvalidContinuationTokenException>(
			() => reader.ReadAsync(new UpstreamReadRequest(seed.DependentJobId, "low", null, null, null, 10, "!!not-a-token"), ct));
	}

	[Fact]
	public async Task PagingReturnsEveryResultExactlyOnceInAStableOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, SnapshotStatus.Accepted, ct);

		var all = (await ReadAsync(provider, seed)).Results.Select(r => r.AcceptedResultId).ToList();
		var paged = new List<Guid>();
		string? token = null;
		var pages = 0;
		do
		{
			var page = await ReadAsync(provider, seed, pageSize: 1, token: token);
			page.Results.Count.ShouldBeLessThanOrEqualTo(1);
			paged.AddRange(page.Results.Select(r => r.AcceptedResultId));
			token = page.ContinuationToken;
			pages++;
		}
		while (token is not null && pages < 10);

		paged.ShouldBe(all);
		paged.Distinct().Count().ShouldBe(paged.Count);
	}

	[Fact]
	public async Task SegmentIdAndRangeFiltersNarrowResultsAndCoverage()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, SnapshotStatus.Accepted, ct);

		var byId = await ReadAsync(provider, seed, segmentIds: [$"{seed.RecordingVersionId}-2", $"{seed.RecordingVersionId}-3"]);
		byId.Results.Select(r => r.WindowStartMs).ShouldBe([SegmentMs]);
		byId.Coverage.Select(c => c.State).ShouldBe([CoverageState.Accepted, CoverageState.Blocked]);

		// Half-open range [300000, 600000) selects exactly the second segment.
		var byRange = await ReadAsync(provider, seed, start: SegmentMs, end: 2 * SegmentMs);
		byRange.Results.Select(r => r.WindowStartMs).ShouldBe([SegmentMs]);
		byRange.Coverage.Count.ShouldBe(1);
	}
}
