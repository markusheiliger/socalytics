using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Facts;

public sealed class FactReadEndpointTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record SeededRun(Guid RunId, Guid DetectNode, Guid[] ResultIds);

	private static string NodeSnapshot(string key, string capability, string schema) =>
		$$"""
		{"nodeKey":"{{key}}","capabilityId":"{{capability}}","schemaVersion":"{{schema}}","analystProfileId":"profile-{{key}}",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":"{{DigestA}}"}
		""";

	// The detect node has three segment jobs (two accepted, one failed); the twin node shares its capability; the match node has no jobs.
	private static async Task<SeededRun> SeedAsync(FinalizationSetup s, CancellationToken ct)
	{
		var detect = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			s.Hierarchy.TeamId, s.Hierarchy.MatchId, Guid.NewGuid(), DateTimeOffset.UtcNow, "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[
				new WorkflowNodeRecord(detect, "detect", "cap.detect", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, NodeSnapshot("detect", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(Guid.CreateVersion7(), "twin", "cap.detect", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 1, WorkflowNodeState.Waiting, null, default, NodeSnapshot("twin", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(Guid.CreateVersion7(), "unused", "cap.other", "2.0.0", CapabilityTier.HighLevel, ExecutionScope.Match, 2, WorkflowNodeState.Waiting, null, default, NodeSnapshot("unused", "cap.other", "2.0.0")),
			],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var jobs = new[] { 1, 2, 3 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, detect, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();

		Guid runId;
		await using (var scope = s.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([.. jobs.Select(j => j with { RunId = runId })], ct);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(s.Db.AppConnectionString);
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
				Guid.NewGuid(), job.Id, runId, detect, attempt, "results/key", DigestA, "cap.detect", "1.0.0", "1.0.0",
				DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
			await using var scope = s.Factory.Services.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await uow.CommitAsync(ct);
			results.Add(result.Id);
		}

		await connection.ExecuteAsync(new CommandDefinition(
			"UPDATE socalytics.analysis_logical_jobs SET state = 'Failed', failure_reason = 'attempts-exhausted' WHERE id = @id",
			new { id = jobs[2].Id }, cancellationToken: ct));
		return new SeededRun(runId, detect, [.. results]);
	}

	private static async Task InsertFactsAsync(FinalizationSetup s, SeededRun seed, Guid resultId, CancellationToken ct, params (int Ordinal, long Start, long End)[] facts)
	{
		var records = facts.Select(f => new IndexedFactRecord(
			resultId, f.Ordinal, seed.RunId, seed.DetectNode, s.Hierarchy.TeamId, s.Hierarchy.MatchId, "cap.detect", "1.0.0", "1.0.0",
			"foundational-facts", "pass", f.Start, f.End, 0.5, $$"""{"o":{{f.Ordinal}}}""")).ToList();
		await using var scope = s.Factory.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(records, ct);
		await uow.CommitAsync(ct);
	}

	private static string Path(Guid run, string query = "nodeKey=detect") =>
		$"/api/v1/analysis/runs/{run}/facts?capabilityId=cap.detect&capabilityVersion=1.0.0&schemaVersion=1.0.0&{query}";

	private static async Task<JsonElement> ReadAsync(HttpClient client, string path, CancellationToken ct)
	{
		using var response = await client.GetAsync(path, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	private static async Task<ApiSession> MemberAsync(FinalizationSetup s, string name, string role, Guid team, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(s.Db, name, cancellationToken: ct);
		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await c.ExecuteAsync(new CommandDefinition(
			"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@id, @team, @role, now(), @id)",
			new { id, team, role }, cancellationToken: ct));
		return await ApiSession.SignInAsync(s.Factory, name, TestMembers.DefaultPassword, ct);
	}

	[Fact]
	public async Task AccessFollowsTeamReadPermission()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);
		var viewer = await MemberAsync(s, "viewer-1", "viewer", s.Hierarchy.TeamId, ct);
		var (otherTeam, _) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var outsider = await MemberAsync(s, "coach-2", "coach", otherTeam, ct);

		foreach (var session in new[] { s.Admin, s.Coach, viewer })
		{
			(await ReadAsync(session.Client, Path(seed.RunId), ct)).GetProperty("run").GetProperty("runId").GetGuid().ShouldBe(seed.RunId);
		}

		foreach (var id in new[] { seed.RunId, Guid.NewGuid() })
		{
			using var hidden = await outsider.Client.GetAsync(Path(id), ct);
			hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using var missing = await s.Coach.Client.GetAsync(Path(Guid.NewGuid()), ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var anonymous = s.Factory.CreateApiClient();
		using var unauthorized = await anonymous.GetAsync(Path(seed.RunId), ct);
		unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task PagesWithoutDuplicatesOrGapsAndFiltersByOverlap()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);
		var other = await SeedAsync(s, ct);
		await InsertFactsAsync(s, seed, seed.ResultIds[0], ct, (0, 0, 100), (1, 0, 100), (2, 50, 150));
		await InsertFactsAsync(s, seed, seed.ResultIds[1], ct, (0, 0, 100), (1, 300_000, 300_100), (2, 0, 50));
		await InsertFactsAsync(s, other, other.ResultIds[0], ct, (0, 0, 100));

		var keys = new List<(Guid, int)>();
		string? token = null;
		var pages = 0;
		do
		{
			var body = await ReadAsync(s.Coach.Client, Path(seed.RunId, "nodeKey=detect&pageSize=2" + (token is null ? "" : $"&continuationToken={token}")), ct);
			pages++;
			keys.AddRange(body.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("acceptedResultId").GetGuid(), i.GetProperty("ordinal").GetInt32())));
			token = body.GetProperty("continuationToken").GetString();
			body.GetProperty("coverage").GetArrayLength().ShouldBe(3);
		}
		while (token is not null);

		pages.ShouldBe(3);
		keys.Count.ShouldBe(6);
		keys.Distinct().Count().ShouldBe(6);
		keys.ShouldAllBe(k => k.Item1 == seed.ResultIds[0] || k.Item1 == seed.ResultIds[1]);

		var overlap = await ReadAsync(s.Coach.Client, Path(seed.RunId, "nodeKey=detect&fromMs=100&toMs=300000"), ct);
		overlap.GetProperty("items").GetArrayLength().ShouldBe(1);
		overlap.GetProperty("items")[0].GetProperty("startMs").GetInt64().ShouldBe(50);
		overlap.GetProperty("coverage").GetArrayLength().ShouldBe(1);
		overlap.GetProperty("coverage")[0].GetProperty("state").GetString().ShouldBe("accepted");
	}

	[Fact]
	public async Task ReportsCoverageProvenanceAndNoInternalIdentifiers()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);

		using var response = await s.Coach.Client.GetAsync(Path(seed.RunId), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		var body = JsonDocument.Parse(text).RootElement;
		body.GetProperty("items").GetArrayLength().ShouldBe(0);
		body.GetProperty("coverage").EnumerateArray().Select(c => c.GetProperty("state").GetString())
			.ShouldBe(["accepted", "accepted", "failed"]);
		body.GetProperty("run").GetProperty("state").GetString().ShouldBe("Running");
		var provenance = body.GetProperty("stream").GetProperty("provenance");
		provenance.GetProperty("workflow").GetProperty("id").GetString().ShouldBe("wf");
		provenance.GetProperty("analystProfile").GetProperty("id").GetString().ShouldBe("profile-detect");
		provenance.GetProperty("imageDigest").GetString().ShouldBe(DigestB);
		provenance.GetProperty("modelDigest").GetString().ShouldBe(DigestA);
		foreach (var forbidden in new[] { "segmentid", "segmentnumber", "recording", "mapping", "segmentation", "storage", "results/key" })
		{
			text.ToLowerInvariant().ShouldNotContain(forbidden);
		}

		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await c.ExecuteAsync(new CommandDefinition(
			"UPDATE socalytics.analysis_logical_jobs SET state = 'Blocked' WHERE run_id = @run AND state = 'Failed'", new { run = seed.RunId }, cancellationToken: ct));
		var blocked = await ReadAsync(s.Coach.Client, Path(seed.RunId), ct);
		blocked.GetProperty("coverage")[2].GetProperty("state").GetString().ShouldBe("blocked");
	}

	[Fact]
	public async Task RejectsUnknownAmbiguousAndInvalidSelections()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);

		async Task<JsonElement> ProblemAsync(string path, HttpStatusCode status)
		{
			using var response = await s.Coach.Client.GetAsync(path, ct);
			response.StatusCode.ShouldBe(status);
			return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		}

		(await ProblemAsync(Path(seed.RunId, ""), HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("analysis-capability-ambiguous");
		(await ProblemAsync(Path(seed.RunId, "nodeKey=unused"), HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("analysis-capability-not-in-run");
		(await ProblemAsync(Path(seed.RunId, "nodeKey=detect").Replace("schemaVersion=1.0.0", "schemaVersion=2.0.0"), HttpStatusCode.NotFound))
			.GetProperty("code").GetString().ShouldBe("analysis-capability-not-in-run");
		(await ProblemAsync($"/api/v1/analysis/runs/{seed.RunId}/facts", HttpStatusCode.BadRequest)).GetProperty("errors").GetArrayLength().ShouldBe(3);

		foreach (var query in new[] { "pageSize=0", "pageSize=501", "fromMs=-1", "fromMs=10&toMs=10", "toMs=0", "continuationToken=abc" })
		{
			await ProblemAsync(Path(seed.RunId, "nodeKey=detect&" + query), HttpStatusCode.BadRequest);
		}

		await InsertFactsAsync(s, seed, seed.ResultIds[0], ct, (0, 0, 100), (1, 100, 200));
		var first = await ReadAsync(s.Coach.Client, Path(seed.RunId, "nodeKey=detect&pageSize=1"), ct);
		var token = first.GetProperty("continuationToken").GetString();
		await ProblemAsync(Path(seed.RunId, $"nodeKey=detect&pageSize=1&fromMs=1&continuationToken={token}"), HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task IfNoneMatchReturnsNotModifiedUntilFactsChange()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);
		var path = Path(seed.RunId);

		using var ok = await s.Coach.Client.GetAsync(path, ct);
		ok.Headers.ETag!.IsWeak.ShouldBeTrue();
		var etag = ok.Headers.ETag.ToString();

		async Task<HttpStatusCode> ConditionalAsync()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, path);
			request.Headers.TryAddWithoutValidation("If-None-Match", etag);
			using var response = await s.Coach.Client.SendAsync(request, ct);
			return response.StatusCode;
		}

		(await ConditionalAsync()).ShouldBe(HttpStatusCode.NotModified);
		await InsertFactsAsync(s, seed, seed.ResultIds[0], ct, (0, 0, 100));
		(await ConditionalAsync()).ShouldBe(HttpStatusCode.OK);
	}
}
