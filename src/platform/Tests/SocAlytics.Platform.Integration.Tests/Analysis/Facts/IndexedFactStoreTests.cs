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

namespace SocAlytics.Platform.Integration.Tests.Analysis.Facts;

public sealed class IndexedFactStoreTests(PostgresContainerFixture postgres)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record Seed(Guid RunId, Guid NodeId, Guid AcceptedResultId);

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

		return new Seed(runId, high, result.Id);
	}

	private static IndexedFactRecord Fact(
		Seed seed, int ordinal, long start = 0, long end = 1000, double confidence = 0.5,
		string payload = """{"x":1}""", string outputType = "foundational-facts") =>
		new(seed.AcceptedResultId, ordinal, seed.RunId, seed.NodeId, Guid.NewGuid(), Guid.NewGuid(), "cap.high", "1", "1",
			outputType, "pass", start, end, confidence, payload);

	private static async Task InsertAsync(IServiceProvider root, IReadOnlyList<IndexedFactRecord> facts, CancellationToken ct)
	{
		await using var scope = root.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(facts, ct);
		await uow.CommitAsync(ct);
	}

	[Fact]
	public async Task InsertsFactsInBatchesAndKeepsTheirValues()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;

		var facts = Enumerable.Range(0, 2500).Select(i => Fact(seed, i, i, i + 10, 1, $$"""{"i":{{i}}}""")).ToList();
		await InsertAsync(provider, facts, ct);
		await InsertAsync(provider, [], ct);

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
			"SELECT count(*) FROM socalytics.analysis_indexed_facts WHERE accepted_result_id = @id", new { id = seed.AcceptedResultId }, cancellationToken: ct)))
			.ShouldBe(2500);
		var row = await connection.QuerySingleAsync<(long Start, long End, double Confidence, string Payload, string Kind)>(new CommandDefinition(
			"SELECT start_ms AS Start, end_ms AS \"End\", confidence AS Confidence, payload::text AS Payload, kind AS Kind FROM socalytics.analysis_indexed_facts WHERE ordinal = 7", cancellationToken: ct));
		row.Start.ShouldBe(7);
		row.End.ShouldBe(17);
		row.Confidence.ShouldBe(1);
		row.Payload.ShouldContain("\"i\"");
		row.Kind.ShouldBe("pass");
	}

	[Theory]
	[InlineData(-1, 10, 0.5, """{}""", "foundational-facts")]
	[InlineData(10, 10, 0.5, """{}""", "foundational-facts")]
	[InlineData(10, 5, 0.5, """{}""", "foundational-facts")]
	[InlineData(0, 10, -0.1, """{}""", "foundational-facts")]
	[InlineData(0, 10, 1.1, """{}""", "foundational-facts")]
	[InlineData(0, 10, 0.5, """[]""", "foundational-facts")]
	[InlineData(0, 10, 0.5, """{}""", "other")]
	public async Task InvalidValuesAreRefusedByTheChecks(long start, long end, double confidence, string payload, string outputType)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;

		await using var scope = provider.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() =>
			scope.ServiceProvider.GetRequiredService<IIndexedFactStore>()
				.InsertAsync([Fact(seed, 0, start, end, confidence, payload, outputType)], ct));
		ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task DuplicateKeyIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await InsertAsync(provider, [Fact(seed, 0)], ct);

		await using var scope = provider.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() =>
			scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync([Fact(seed, 0, 5, 9)], ct));
		ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
	}

	[Theory]
	[InlineData("UPDATE", "SET kind = 'other'")]
	[InlineData("DELETE", "")]
	public async Task FactsRejectUpdateAndDelete(string verb, string set)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider, seed) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await InsertAsync(provider, [Fact(seed, 0)], ct);

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var sql = verb == "UPDATE" ? $"UPDATE socalytics.analysis_indexed_facts {set}" : "DELETE FROM socalytics.analysis_indexed_facts";
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
	}
}
