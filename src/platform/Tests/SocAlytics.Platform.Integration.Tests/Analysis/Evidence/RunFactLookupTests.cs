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

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class RunFactLookupTests(PostgresContainerFixture postgres)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record SeededRun(Guid RunId, Guid ResultId);

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

	private static async Task<SeededRun> SeedAsync(IsolatedDatabase db, ServiceProvider provider, CancellationToken ct)
	{
		var node = Guid.CreateVersion7();
		var snapshotJson = $$"""
			{"nodeKey":"detect","capabilityId":"cap.detect","schemaVersion":"1.0.0","analystProfileId":"profile-detect",
			 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":null}
			""";
		var run = new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[new WorkflowNodeRecord(node, "detect", "cap.detect", "1", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, snapshotJson)],
			[]);
		var jobSnapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var job = new NewLogicalJob(
			Guid.CreateVersion7(), default, node, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, 1, LogicalJobState.Waiting, jobSnapshot);

		Guid runId;
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([job with { RunId = runId }], ct);
			await uow.CommitAsync(ct);
		}

		var occurrence = Guid.NewGuid();
		var attempt = Guid.NewGuid();
		await using (var connection = new NpgsqlConnection(db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
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
		}

		var result = new AcceptedResultRecord(
			Guid.NewGuid(), job.Id, runId, node, attempt, "results/key", DigestA, "cap.detect", "1", "1.0.0",
			DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			var fact = new IndexedFactRecord(
				result.Id, 0, runId, node, Guid.NewGuid(), Guid.NewGuid(), "cap.detect", "1", "1.0.0",
				"foundational-facts", "pass", 1_000, 4_500, 0.5, """{"o":0}""");
			await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync([fact], ct);
			await uow.CommitAsync(ct);
		}

		return new SeededRun(runId, result.Id);
	}

	private static async Task<FactTimeRange?> LookupAsync(IServiceProvider root, Guid runId, Guid resultId, int ordinal, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<IFactQueries>().GetRunFactAsync(runId, resultId, ordinal, ct);
	}

	[Fact]
	public async Task ReturnsTheStartAndEndOfAFactOfTheRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);

		var fact = await LookupAsync(provider, seed.RunId, seed.ResultId, 0, ct);

		fact.ShouldBe(new FactTimeRange(1_000, 4_500));
	}

	[Fact]
	public async Task ReturnsNullForAFactOfAnotherRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);
		var other = await SeedAsync(db, provider, ct);

		(await LookupAsync(provider, other.RunId, seed.ResultId, 0, ct)).ShouldBeNull();
		(await LookupAsync(provider, Guid.NewGuid(), seed.ResultId, 0, ct)).ShouldBeNull();
	}

	[Fact]
	public async Task ReturnsNullForAnUnknownOrdinalOrResult()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var seed = await SeedAsync(db, provider, ct);

		(await LookupAsync(provider, seed.RunId, seed.ResultId, 1, ct)).ShouldBeNull();
		(await LookupAsync(provider, seed.RunId, Guid.NewGuid(), 0, ct)).ShouldBeNull();
	}
}
