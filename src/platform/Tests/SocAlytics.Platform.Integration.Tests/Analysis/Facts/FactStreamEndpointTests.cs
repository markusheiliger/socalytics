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

public sealed class FactStreamEndpointTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record SeededRun(Guid RunId, Guid DetectNode, Guid[] ResultIds);

	private static string NodeSnapshot(string key, string capability, string schema) =>
		$$"""
		{"nodeKey":"{{key}}","capabilityId":"{{capability}}","schemaVersion":"{{schema}}","analystProfileId":"profile-{{key}}",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":"{{DigestA}}"}
		""";

	// The detect node has three segment jobs (two accepted, one failed); the pending node has two waiting jobs; the match node has no jobs.
	private static async Task<SeededRun> SeedAsync(FinalizationSetup s, CancellationToken ct)
	{
		var detect = Guid.CreateVersion7();
		var pending = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			s.Hierarchy.TeamId, s.Hierarchy.MatchId, Guid.NewGuid(), "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[
				new WorkflowNodeRecord(detect, "detect", "cap.detect", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Active, null, default, NodeSnapshot("detect", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(pending, "pending", "cap.pending", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 1, WorkflowNodeState.Waiting, null, default, NodeSnapshot("pending", "cap.pending", "1.0.0")),
				new WorkflowNodeRecord(Guid.CreateVersion7(), "unused", "cap.other", "2.0.0", CapabilityTier.HighLevel, ExecutionScope.Match, 2, WorkflowNodeState.Waiting, null, default, NodeSnapshot("unused", "cap.other", "2.0.0")),
			],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var jobs = new[] { 1, 2, 3 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, detect, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();
		var pendingJobs = new[] { 1, 2 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, pending, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();

		Guid runId;
		await using (var scope = s.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([.. jobs.Concat(pendingJobs).Select(j => j with { RunId = runId })], ct);
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

	private static string Path(Guid run) => $"/api/v1/analysis/runs/{run}/fact-streams";

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
	public async Task ListsOneEntryPerNodeWithCountsAndCoverage()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = await SeedAsync(s, ct);
		await InsertFactsAsync(s, seed, seed.ResultIds[0], ct, (0, 0, 100), (1, 100, 200));
		await InsertFactsAsync(s, seed, seed.ResultIds[1], ct, (0, 300_000, 300_100));

		using var response = await s.Coach.Client.GetAsync(Path(seed.RunId), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
		var body = JsonDocument.Parse(text).RootElement;
		body.GetProperty("run").GetProperty("runId").GetGuid().ShouldBe(seed.RunId);
		var items = body.GetProperty("items").EnumerateArray().ToList();
		items.Select(i => i.GetProperty("nodeKey").GetString()).ShouldBe(["detect", "pending", "unused"]);

		var detect = items[0];
		detect.GetProperty("required").GetBoolean().ShouldBeTrue();
		detect.GetProperty("outputType").GetString().ShouldBe("foundational-facts");
		detect.GetProperty("executionScope").GetString().ShouldBe("segment");
		detect.GetProperty("capability").GetProperty("id").GetString().ShouldBe("cap.detect");
		detect.GetProperty("provenance").GetProperty("workflow").GetProperty("id").GetString().ShouldBe("wf");
		detect.GetProperty("provenance").GetProperty("analystProfile").GetProperty("id").GetString().ShouldBe("profile-detect");
		detect.GetProperty("provenance").GetProperty("imageDigest").GetString().ShouldBe(DigestB);
		detect.GetProperty("factCount").GetInt64().ShouldBe(3);
		Coverage(detect).ShouldBe([2, 0, 1, 0, 0]);

		// A required node without accepted results reports missing windows and no facts.
		items[1].GetProperty("required").GetBoolean().ShouldBeTrue();
		items[1].GetProperty("factCount").GetInt64().ShouldBe(0);
		Coverage(items[1]).ShouldBe([0, 2, 0, 0, 0]);

		// A node no job was created for is not required and reports no facts.
		items[2].GetProperty("required").GetBoolean().ShouldBeFalse();
		items[2].GetProperty("factCount").GetInt64().ShouldBe(0);
		Coverage(items[2]).Take(4).ShouldBe([0, 0, 0, 0]);

		// The count equals the facts read through the fact listing.
		var read = await ReadAsync(
			s.Coach.Client,
			$"/api/v1/analysis/runs/{seed.RunId}/facts?capabilityId=cap.detect&capabilityVersion=1.0.0&schemaVersion=1.0.0&nodeKey=detect&pageSize=500", ct);
		read.GetProperty("items").GetArrayLength().ShouldBe(3);

		foreach (var forbidden in new[] { "segmentid", "segmentnumber", "recording", "mapping", "segmentation", "storage", "results/key" })
		{
			text.ToLowerInvariant().ShouldNotContain(forbidden);
		}
	}

	private static int[] Coverage(JsonElement entry)
	{
		var c = entry.GetProperty("coverage");
		return [.. new[] { "accepted", "missing", "failed", "blocked", "skipped" }.Select(k => c.GetProperty(k).GetInt32())];
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
			(await ReadAsync(session.Client, Path(seed.RunId), ct)).GetProperty("items").GetArrayLength().ShouldBe(3);
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
