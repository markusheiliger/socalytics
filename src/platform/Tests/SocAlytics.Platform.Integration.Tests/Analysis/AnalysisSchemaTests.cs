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
			Guid.NewGuid(), Guid.NewGuid(), setId, DateTimeOffset.UtcNow, "wf", workflowVersion, ["cap.high", "cap.low"], "all-requested-capabilities",
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
		services.AddTestHostEnvironment();
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
	public async Task EdgeCoveragePolicyDefaultsToCompleteAndRoundTrips()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var run = NewRun(Guid.NewGuid(), "1", out var low, out var high);
		var partial = run with
		{
			Edges = [run.Edges[0] with { CoveragePolicy = CoveragePolicy.Partial(0.5m) }],
		};
		var completeId = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out _), ct);
		var partialId = await InsertAsync(provider, partial, ct);

		await using var scope = provider.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>();
		var complete = (await store.LoadAsync(completeId, ct))!.Edges.Single();
		complete.CoveragePolicy.Mode.ShouldBe(CoverageMode.Complete);
		complete.CoveragePolicy.MinimumAcceptedFraction.ShouldBeNull();
		var loaded = (await store.LoadAsync(partialId, ct))!.Edges.Single();
		loaded.UpstreamNodeId.ShouldBe(low);
		loaded.DependentNodeId.ShouldBe(high);
		loaded.CoveragePolicy.Mode.ShouldBe(CoverageMode.Partial);
		loaded.CoveragePolicy.MinimumAcceptedFraction.ShouldBe(0.5m);

		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
			"SELECT count(*) FROM socalytics.analysis_dependency_edges WHERE coverage_mode = 'complete' AND minimum_accepted_fraction IS NULL", cancellationToken: ct))).ShouldBe(1);
		(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
			"SELECT count(*) FROM socalytics.analysis_dependency_edges WHERE coverage_mode = 'partial' AND minimum_accepted_fraction = 0.5", cancellationToken: ct))).ShouldBe(1);
	}

	[Theory]
	[InlineData("'bogus', NULL")]
	[InlineData("'complete', 0.5")]
	[InlineData("'partial', 0")]
	[InlineData("'partial', 1.5")]
	public async Task EdgeCoverageChecksRejectInvalidCombinations(string values)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out _), ct);
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
			$"""
			INSERT INTO socalytics.analysis_dependency_edges (run_id, upstream_node_id, dependent_node_id, kind, predicate, coverage_mode, minimum_accepted_fraction)
			SELECT run_id, dependent_node_id, upstream_node_id, 'required', NULL, {values} FROM socalytics.analysis_dependency_edges
			""", cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task EdgeCoverageAcceptsPartialWithoutAFraction()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var run = NewRun(Guid.NewGuid(), "1", out _, out _);
		var id = await InsertAsync(provider, run with { Edges = [run.Edges[0] with { CoveragePolicy = CoveragePolicy.Partial() }] }, ct);
		await using var scope = provider.CreateAsyncScope();
		var loaded = (await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LoadAsync(id, ct))!.Edges.Single();
		loaded.CoveragePolicy.Mode.ShouldBe(CoverageMode.Partial);
		loaded.CoveragePolicy.MinimumAcceptedFraction.ShouldBeNull();
	}

	[Fact]
	public async Task LogicalJobFailureReasonAcceptsTheCoverageBelowMinimumReason()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var run = NewRun(Guid.NewGuid(), "1", out _, out var high);
		var runId = await InsertAsync(provider, run, ct);
		await InsertJobsAsync(provider, ct, MatchJob(runId, high));
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await connection.ExecuteAsync(new CommandDefinition(
			"UPDATE socalytics.analysis_logical_jobs SET failure_reason = 'upstream-coverage-below-minimum'", cancellationToken: ct));
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
			"UPDATE socalytics.analysis_logical_jobs SET failure_reason = 'bogus'", cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
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

	private static readonly LogicalJobSnapshotRecord JobSnapshot =
		new(3, 60, 120, 600, "sha-256:" + new string('b', 64), "sha-256:" + new string('c', 64), null, "{}", "1");

	private static NewLogicalJob SegmentJob(Guid runId, Guid nodeId, Guid? version, string? mapping, string? policy, int? number) =>
		new(Guid.CreateVersion7(), runId, nodeId, ExecutionScope.Segment, version, mapping, policy, number, LogicalJobState.Waiting, JobSnapshot);

	private static NewLogicalJob MatchJob(Guid runId, Guid nodeId, int? number = null) =>
		new(Guid.CreateVersion7(), runId, nodeId, ExecutionScope.Match, null, null, null, number, LogicalJobState.Waiting, JobSnapshot);

	private static async Task InsertJobsAsync(IServiceProvider root, CancellationToken ct, params NewLogicalJob[] jobs)
	{
		await using var scope = root.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync(jobs, ct);
		await uow.CommitAsync(ct);
	}

	[Fact]
	public async Task LogicalJobIdentityAndSegmentFieldRulesAreEnforced()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var run = NewRun(Guid.NewGuid(), "1", out var low, out var high);
		var runId = await InsertAsync(provider, run, ct);
		var version = Guid.NewGuid();
		var digest = "sha-256:" + new string('d', 64);

		await InsertJobsAsync(provider, ct, SegmentJob(runId, low, version, digest, digest, 1), MatchJob(runId, high));
		await InsertJobsAsync(provider, ct, SegmentJob(runId, low, version, digest, digest, 2));

		var dupSegment = await Should.ThrowAsync<PostgresException>(() => InsertJobsAsync(provider, ct, SegmentJob(runId, low, version, digest, digest, 1)));
		dupSegment.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		var dupMatch = await Should.ThrowAsync<PostgresException>(() => InsertJobsAsync(provider, ct, MatchJob(runId, high)));
		dupMatch.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		var missing = await Should.ThrowAsync<PostgresException>(() => InsertJobsAsync(provider, ct, SegmentJob(runId, low, version, digest, digest, null)));
		missing.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
		var extra = await Should.ThrowAsync<PostgresException>(() => InsertJobsAsync(provider, ct, MatchJob(runId, high, 5)));
		extra.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task AttemptConstraintsAndFencingSequenceWork()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var runId = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out var high), ct);
		var job = MatchJob(runId, high);
		await InsertJobsAsync(provider, ct, job);

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var occurrences = new[] { Guid.NewGuid(), Guid.NewGuid() };
		for (var i = 0; i < occurrences.Length; i++)
		{
			await connection.ExecuteAsync(new CommandDefinition(
				"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@id, @job, @seq, 'initial', now())",
				new { id = occurrences[i], job = job.Id, seq = i + 1 }, cancellationToken: ct));
		}

		async Task InsertAttempt(Guid occurrence, int number, string state) => await connection.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_execution_attempts
				(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
			VALUES (gen_random_uuid(), @job, @occurrence, @number, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), @state, now(), now(), now())
			""",
			new { job = job.Id, occurrence, number, state }, cancellationToken: ct));

		await InsertAttempt(occurrences[0], 1, "Active");
		var twoActive = await Should.ThrowAsync<PostgresException>(() => InsertAttempt(occurrences[1], 2, "Active"));
		twoActive.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		var dupNumber = await Should.ThrowAsync<PostgresException>(() => InsertAttempt(occurrences[1], 1, "Stale"));
		dupNumber.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		await InsertAttempt(occurrences[1], 2, "Stale");
	}

	[Theory]
	[InlineData("analysis_logical_job_snapshots", "UPDATE", "SET max_attempts = 9")]
	[InlineData("analysis_logical_job_snapshots", "DELETE", "")]
	[InlineData("analysis_logical_job_inputs", "UPDATE", "SET inputs = '[]'::jsonb")]
	[InlineData("analysis_logical_job_inputs", "DELETE", "")]
	[InlineData("analysis_readiness_occurrences", "UPDATE", "SET cause = 'attempt-failed'")]
	[InlineData("analysis_readiness_occurrences", "DELETE", "")]
	public async Task LogicalJobImmutableTablesRejectUpdateAndDelete(string table, string verb, string set)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var runId = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out var high), ct);
		var job = MatchJob(runId, high);
		await InsertJobsAsync(provider, ct, job);
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await connection.ExecuteAsync(new CommandDefinition(
			"INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at) VALUES (@job, '{}'::jsonb, now())",
			new { job = job.Id }, cancellationToken: ct));
		await connection.ExecuteAsync(new CommandDefinition(
			"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (gen_random_uuid(), @job, 1, 'initial', now())",
			new { job = job.Id }, cancellationToken: ct));

		var sql = verb == "UPDATE" ? $"UPDATE socalytics.{table} {set}" : $"DELETE FROM socalytics.{table}";
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
	}

	[Theory]
	[InlineData("UPDATE socalytics.analysis_claim_keys SET idempotency_key = 'x'")]
	[InlineData("DELETE FROM socalytics.analysis_claim_keys")]
	public async Task ClaimKeysRejectUpdateAndDeleteForTheRuntimeRole(string sql)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
		(await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT nextval('socalytics.analysis_fencing_token_seq')", cancellationToken: ct))).ShouldBe(1);
	}

	private static AcceptedResultRecord Result(Guid runId, Guid nodeId, Guid jobId, Guid attemptId) => new(
		Guid.NewGuid(), jobId, runId, nodeId, attemptId, "results/key", "sha-256:" + new string('e', 64), "cap.high", "1", "1",
		"sha-256:" + new string('b', 64), "sha-256:" + new string('c', 64), null, "{}", "{}", [], DateTimeOffset.UtcNow);

	private static async Task<Guid> InsertAttemptAsync(NpgsqlConnection connection, Guid jobId, int number, CancellationToken ct)
	{
		var occurrence = Guid.NewGuid();
		var attempt = Guid.NewGuid();
		await connection.ExecuteAsync(new CommandDefinition(
			"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occurrence, @jobId, @number, 'initial', now())",
			new { occurrence, jobId, number }, cancellationToken: ct));
		await connection.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_execution_attempts
				(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
			VALUES (@attempt, @jobId, @occurrence, @number, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Stale', now(), now(), now())
			""",
			new { attempt, jobId, occurrence, number }, cancellationToken: ct));
		return attempt;
	}

	[Fact]
	public async Task SecondAcceptedResultForOneJobOrOneAttemptIsRejected()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var runId = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out var high), ct);
		var job = MatchJob(runId, high);
		await InsertJobsAsync(provider, ct, job);
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var first = await InsertAttemptAsync(connection, job.Id, 1, ct);
		var second = await InsertAttemptAsync(connection, job.Id, 2, ct);

		async Task Accept(AcceptedResultRecord result)
		{
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await uow.CommitAsync(ct);
		}

		await Accept(Result(runId, high, job.Id, first));
		(await Should.ThrowAsync<PostgresException>(() => Accept(Result(runId, high, job.Id, second))))
			.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		(await Should.ThrowAsync<PostgresException>(() => Accept(Result(runId, high, job.Id, first))))
			.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

		await using var read = provider.CreateAsyncScope();
		var store = read.ServiceProvider.GetRequiredService<IAcceptedResultStore>();
		(await store.GetByJobAsync(job.Id, ct))!.AttemptId.ShouldBe(first);
		(await store.ListByRunAsync(runId, ct)).Count.ShouldBe(1);
	}

	[Fact]
	public async Task ExistingNotificationMessageReturnsTheStoredOutcome()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var messageId = Guid.NewGuid();

		async Task<NotificationReceiptInsertResult> Try(string outcome, string? category)
		{
			await using var scope = provider.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			var result = await scope.ServiceProvider.GetRequiredService<INotificationReceiptStore>().TryInsertAsync(
				new NotificationReceiptRecord(messageId, "recordings-finalized", Guid.NewGuid(), outcome, category, null, DateTimeOffset.UtcNow), ct);
			await uow.CommitAsync(ct);
			return result;
		}

		(await Try("rejected", "schema-invalid")).Inserted.ShouldBeTrue();
		var again = await Try("run-created", null);
		again.Inserted.ShouldBeFalse();
		again.Receipt.Outcome.ShouldBe("rejected");
		again.Receipt.RejectionCategory.ShouldBe("schema-invalid");

		await using var read = provider.CreateAsyncScope();
		(await read.ServiceProvider.GetRequiredService<INotificationReceiptStore>().ListAsync(0, 10, ct)).Count.ShouldBe(1);
	}

	[Theory]
	[InlineData("UPDATE socalytics.analysis_accepted_results SET manifest_reference = 'x'")]
	[InlineData("DELETE FROM socalytics.analysis_accepted_results")]
	[InlineData("UPDATE socalytics.analysis_completion_outcomes SET outcome = 'accepted'")]
	[InlineData("DELETE FROM socalytics.analysis_completion_outcomes")]
	[InlineData("UPDATE socalytics.analysis_notification_receipts SET outcome = 'run-exists'")]
	[InlineData("DELETE FROM socalytics.analysis_notification_receipts")]
	public async Task ResultTablesRejectUpdateAndDeleteForTheRuntimeRole(string sql)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
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

	[Theory]
	[InlineData("recordings-finalized", "schema-invalid", true)]
	[InlineData("analysis-requested", null, true)]
	[InlineData("analysis-requested", "no-finalized-recording-set", true)]
	[InlineData("analysis-requested", "not-configured", true)]
	[InlineData("analysis-requested", "invalid-definition", true)]
	[InlineData("recordings-finalized", "no-finalized-recording-set", true)]
	[InlineData("analysis-requests", null, false)]
	[InlineData("analysis-requested", "unknown-category", false)]
	public async Task ReceiptsAcceptTheAnalysisRequestedTypeAndTheNewRejectionCategories(string type, string? category, bool accepted)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);

		var insert = connection.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_notification_receipts (message_id, message_type, outcome, rejection_category, received_at)
			VALUES (@id, @type, @outcome, @category, now())
			""",
			new { id = Guid.NewGuid(), type, outcome = category is null ? "run-exists" : "rejected", category }, cancellationToken: ct));
		if (accepted)
			(await insert).ShouldBe(1);
		else
			(await Should.ThrowAsync<PostgresException>(() => insert)).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	private sealed record RequestRow(string Key, string Outcome, string? Category, bool WithRun, bool WithSet);

	private static async Task<(Guid Match, Guid Account, Guid Run)> SeedRequestPrerequisitesAsync(
		IsolatedDatabase db, ServiceProvider provider, CancellationToken ct)
	{
		var account = Guid.NewGuid();
		var season = Guid.NewGuid();
		var team = Guid.NewGuid();
		var match = Guid.NewGuid();
		await using (var connection = new NpgsqlConnection(db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await connection.ExecuteAsync(new CommandDefinition(
				"""
				INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, membership_changed_at, created_at)
				VALUES (@account, 'a', 'A', 's', now(), now());
				INSERT INTO socalytics.season (id, name, created_at) VALUES (@season, 'S', now());
				INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES (@team, @season, 'T', now());
				INSERT INTO socalytics.match (id, team_id, opponent_name, kickoff_at, home_away, created_at, created_by_account_id)
				VALUES (@match, @team, 'O', now(), 'home', now(), @account);
				""",
				new { account, season, team, match }, cancellationToken: ct));
		}

		var run = await InsertAsync(provider, NewRun(Guid.NewGuid(), "1", out _, out _), ct);
		return (match, account, run);
	}

	private static async Task InsertRequestAsync(
		string connectionString, Guid match, Guid account, Guid run, RequestRow row, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(connectionString);
		await connection.OpenAsync(ct);
		var requestId = Guid.NewGuid();
		var setId = row.WithSet ? Guid.NewGuid() : (Guid?)null;
		var runId = row.WithRun ? run : (Guid?)null;
		await connection.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_notification_receipts
				(message_id, message_type, recording_set_version_id, outcome, rejection_category, run_id, received_at)
			VALUES (@requestId, 'analysis-requested', @setId, @outcome, @category, @runId, now());
			INSERT INTO socalytics.analysis_run_requests
				(idempotency_key, match_id, requested_by, request_id, outcome, rejection_category, recording_set_version_id, run_id, created_at)
			VALUES (@key, @match, @account, @requestId, @outcome, @category, @setId, @runId, now());
			""",
			new { requestId, setId, runId, outcome = row.Outcome, category = row.Category, key = row.Key, match, account },
			cancellationToken: ct));
	}

	[Theory]
	[InlineData("run-created", null, true, true, true)]
	[InlineData("run-exists", null, true, true, true)]
	[InlineData("rejected", "no-finalized-recording-set", false, false, true)]
	[InlineData("rejected", "not-configured", false, true, true)]
	[InlineData("rejected", "invalid-definition", false, true, true)]
	[InlineData("accepted", null, true, true, false)]
	[InlineData("rejected", null, false, true, false)]
	[InlineData("rejected", "schema-invalid", false, true, false)]
	[InlineData("run-created", "not-configured", true, true, false)]
	[InlineData("rejected", "not-configured", true, true, false)]
	[InlineData("run-created", null, false, true, false)]
	[InlineData("run-exists", null, true, false, false)]
	[InlineData("rejected", "not-configured", false, false, false)]
	public async Task RunRequestConstraintsEnforceTheOutcomeRunAndCategoryRules(
		string outcome, string? category, bool withRun, bool withSet, bool accepted)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var (match, account, run) = await SeedRequestPrerequisitesAsync(db, provider, ct);

		var insert = InsertRequestAsync(db.AppConnectionString, match, account, run, new RequestRow("key-1", outcome, category, withRun, withSet), ct);
		if (accepted)
			await insert;
		else
			(await Should.ThrowAsync<PostgresException>(() => insert)).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Theory]
	[InlineData("a", true)]
	[InlineData("0123456789!~", true)]
	[InlineData("", false)]
	[InlineData("has space", false)]
	[InlineData("tab\there", false)]
	public async Task RunRequestIdempotencyKeysMustBeVisibleAscii(string key, bool accepted)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var (match, account, run) = await SeedRequestPrerequisitesAsync(db, provider, ct);

		var insert = InsertRequestAsync(db.AppConnectionString, match, account, run, new RequestRow(key, "run-created", null, true, true), ct);
		if (accepted)
			await insert;
		else
			(await Should.ThrowAsync<PostgresException>(() => insert)).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Fact]
	public async Task RunRequestKeysAreUniqueAndAtMost255Characters()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var (match, account, run) = await SeedRequestPrerequisitesAsync(db, provider, ct);

		await InsertRequestAsync(db.AppConnectionString, match, account, run, new RequestRow(new string('k', 255), "run-created", null, true, true), ct);
		(await Should.ThrowAsync<PostgresException>(() => InsertRequestAsync(
			db.AppConnectionString, match, account, run, new RequestRow(new string('k', 255), "run-exists", null, true, true), ct)))
			.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
		(await Should.ThrowAsync<PostgresException>(() => InsertRequestAsync(
			db.AppConnectionString, match, account, run, new RequestRow(new string('k', 256), "run-created", null, true, true), ct)))
			.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
	}

	[Theory]
	[InlineData("UPDATE socalytics.analysis_run_requests SET outcome = 'run-exists'")]
	[InlineData("DELETE FROM socalytics.analysis_run_requests")]
	public async Task RunRequestsRejectUpdateAndDeleteForTheRuntimeRoleAndTheOwner(string sql)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, provider) = await CreateAsync(ct);
		await using var dbScope = db;
		await using var providerScope = provider;
		var (match, account, run) = await SeedRequestPrerequisitesAsync(db, provider, ct);
		await InsertRequestAsync(db.AppConnectionString, match, account, run, new RequestRow("key-1", "run-created", null, true, true), ct);

		await using var app = new NpgsqlConnection(db.AppConnectionString);
		await app.OpenAsync(ct);
		(await Should.ThrowAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct))))
			.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

		await using var owner = new NpgsqlConnection(db.MigratorConnectionString);
		await owner.OpenAsync(ct);
		await Should.ThrowAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)));
		(await owner.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM socalytics.analysis_run_requests", cancellationToken: ct))).ShouldBe(1);
	}
}
