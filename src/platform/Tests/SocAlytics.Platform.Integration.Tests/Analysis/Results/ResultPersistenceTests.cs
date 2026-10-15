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

public sealed class ResultPersistenceTests(PostgresContainerFixture postgres)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record Seed(Guid AttemptId, Guid AcceptedResultId);

	private async Task<(IsolatedDatabase Db, ServiceProvider Provider, Seed Seed)> CreateAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString })
			.Build();
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(configuration);
		services.AddInfrastructure();
		var provider = services.BuildServiceProvider();
		return (db, provider, await SeedAsync(db, provider, ct));
	}

	private static async Task<Seed> SeedAsync(IsolatedDatabase db, ServiceProvider provider, CancellationToken ct)
	{
		var high = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wf", "1", ["cap.high"], "all-requested-capabilities",
			AnalysisRunState.Running, null, new AnalysisRunSnapshotRecord("""{"workflow":"wf"}""", DigestA),
			[new WorkflowNodeRecord(high, "high", "cap.high", "1", CapabilityTier.HighLevel, ExecutionScope.Match, 0, WorkflowNodeState.Active, null, default, """{"n":"high"}""")],
			[]);
		var job = new NewLogicalJob(
			Guid.CreateVersion7(), default, high, ExecutionScope.Match, null, null, null, null, LogicalJobState.Waiting,
			new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1"));

		Guid runId;
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([job with { RunId = runId }], ct);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
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
			Guid.NewGuid(), job.Id, runId, high, attempt, "results/key", DigestA, "cap.high", "1", "1",
			DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await uow.CommitAsync(ct);
		}

		return new Seed(attempt, result.Id);
	}

	private static async Task<ResultDeclarationOutcome> DeclareAsync(
		IServiceProvider root, Guid attemptId, string name, long size, string digest, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var outcome = await scope.ServiceProvider.GetRequiredService<IResultDeclarationStore>()
			.DeclareAsync(new ResultObjectDeclaration(attemptId, name, size, digest, DateTimeOffset.UtcNow), ct);
		await uow.CommitAsync(ct);
		return outcome;
	}

	[Fact]
	public async Task DeclarationsInsertAndReadBack()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;

		(await DeclareAsync(provider, seed.AttemptId, "manifest.json", 10, DigestA, ct)).ShouldBe(ResultDeclarationOutcome.Declared);
		(await DeclareAsync(provider, seed.AttemptId, "facts.parquet", 2048, DigestB, ct)).ShouldBe(ResultDeclarationOutcome.Declared);

		await using var scope = provider.CreateAsyncScope();
		var list = await scope.ServiceProvider.GetRequiredService<IResultDeclarationStore>().ListByAttemptAsync(seed.AttemptId, ct);
		list.Select(d => d.Name).ShouldBe(["facts.parquet", "manifest.json"]);
		list[0].SizeBytes.ShouldBe(2048);
		list[0].Digest.ShouldBe(DigestB);
		list[1].Digest.ShouldBe(DigestA);
	}

	[Fact]
	public async Task RepeatedDeclarationWithTheSameDigestIsIdempotentAndADifferentDigestIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;

		(await DeclareAsync(provider, seed.AttemptId, "facts.parquet", 5, DigestA, ct)).ShouldBe(ResultDeclarationOutcome.Declared);
		(await DeclareAsync(provider, seed.AttemptId, "facts.parquet", 5, DigestA, ct)).ShouldBe(ResultDeclarationOutcome.AlreadyDeclared);
		(await DeclareAsync(provider, seed.AttemptId, "facts.parquet", 5, DigestB, ct)).ShouldBe(ResultDeclarationOutcome.DigestConflict);

		await using var scope = provider.CreateAsyncScope();
		var list = await scope.ServiceProvider.GetRequiredService<IResultDeclarationStore>().ListByAttemptAsync(seed.AttemptId, ct);
		list.Count.ShouldBe(1);
		list[0].Digest.ShouldBe(DigestA);
	}

	[Fact]
	public async Task ManifestInsertsAndReadsBackOnceAndRejectsASecondRow()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var manifest = new AcceptedResultManifestRecord(
			seed.AcceptedResultId, "1.0.0", "foundational-facts", 0, 60000, 3,
			$$"""[{"name":"facts.parquet","digest":"{{DigestA}}","sizeBytes":5,"mediaType":"application/vnd.apache.parquet"}]""",
			"[]");

		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().InsertAsync(manifest, ct);
			await uow.CommitAsync(ct);
		}

		await using (var scope = provider.CreateAsyncScope())
		{
			var loaded = await scope.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().GetAsync(seed.AcceptedResultId, ct);
			loaded.ShouldNotBeNull();
			loaded.ContractVersion.ShouldBe("1.0.0");
			loaded.OutputType.ShouldBe("foundational-facts");
			loaded.ValidityStartMs.ShouldBe(0);
			loaded.ValidityEndMs.ShouldBe(60000);
			loaded.FactCount.ShouldBe(3);
			loaded.ArtifactsJson.ShouldContain("facts.parquet");
			loaded.UpstreamLineageJson.ShouldBe("[]");
		}

		await using var again = provider.CreateAsyncScope();
		await using var uow2 = await again.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		(await Should.ThrowAsync<PostgresException>(() =>
			again.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().InsertAsync(manifest, ct)))
			.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
	}

	[Fact]
	public async Task InvalidManifestValuesAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;

		foreach (var (outputType, factCount) in new[] { ("other", 0), ("soccer-concepts", -1) })
		{
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			var record = new AcceptedResultManifestRecord(seed.AcceptedResultId, "1.0.0", outputType, 0, 10, factCount, "[]", "[]");
			(await Should.ThrowAsync<PostgresException>(() =>
				scope.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().InsertAsync(record, ct)))
				.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
		}
	}

	[Theory]
	[InlineData("analysis_result_object_declarations", "UPDATE", "SET size_bytes = 99")]
	[InlineData("analysis_result_object_declarations", "DELETE", "")]
	[InlineData("analysis_accepted_result_manifests", "UPDATE", "SET fact_count = 99")]
	[InlineData("analysis_accepted_result_manifests", "DELETE", "")]
	public async Task ResultTablesRejectUpdateAndDelete(string table, string verb, string set)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await DeclareAsync(provider, seed.AttemptId, "manifest.json", 10, DigestA, ct);
		await using (var scope = provider.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultManifestStore>().InsertAsync(
				new AcceptedResultManifestRecord(seed.AcceptedResultId, "1.0.0", "foundational-facts", 0, 10, 1, "[]", "[]"), ct);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var sql = verb == "UPDATE" ? $"UPDATE socalytics.{table} {set}" : $"DELETE FROM socalytics.{table}";
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
	}
}
