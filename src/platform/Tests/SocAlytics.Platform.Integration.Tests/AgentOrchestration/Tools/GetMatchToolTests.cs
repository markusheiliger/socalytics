using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

public sealed class GetMatchToolTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string GetMatch = "get-match";
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private sealed record SeededRun(Guid RunId, Guid DetectNode, Guid[] ResultIds);

	private sealed record Attempt(Guid AttemptId, string FencingToken, string Token);

	private static string NodeSnapshot(string key, string capability, string schema) =>
		$$"""
		{"nodeKey":"{{key}}","capabilityId":"{{capability}}","schemaVersion":"{{schema}}","analystProfileId":"profile-{{key}}",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":"{{DigestA}}"}
		""";

	// The detect node has three segment jobs (two accepted, one failed); the unused node holds a capability other than the one asked for.
	private static async Task<SeededRun> SeedRunAsync(AgentOrchestrationFixture f, DateTimeOffset finalizedAt, CancellationToken ct)
	{
		var detect = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			f.Hierarchy.TeamId, f.Hierarchy.MatchId, Guid.NewGuid(), finalizedAt, "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Completed, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[
				new WorkflowNodeRecord(detect, "detect", "cap.detect", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Completed, null, default, NodeSnapshot("detect", "cap.detect", "1.0.0")),
				new WorkflowNodeRecord(Guid.CreateVersion7(), "unused", "cap.other", "2.0.0", CapabilityTier.HighLevel, ExecutionScope.Match, 1, WorkflowNodeState.Waiting, null, default, NodeSnapshot("unused", "cap.other", "2.0.0")),
			],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var jobs = new[] { 1, 2, 3 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, detect, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();

		Guid runId;
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([.. jobs.Select(j => j with { RunId = runId })], ct);
			await uow.CommitAsync(ct);
		}

		await using var connection = new NpgsqlConnection(f.Db.AppConnectionString);
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
			await using var scope = f.Factory.Services.CreateAsyncScope();
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

	private static async Task InsertFactsAsync(
		AgentOrchestrationFixture f, SeededRun seed, Guid resultId, CancellationToken ct, params (int Ordinal, long Start, long End)[] facts)
	{
		var records = facts.Select(x => new IndexedFactRecord(
			resultId, x.Ordinal, seed.RunId, seed.DetectNode, f.Hierarchy.TeamId, f.Hierarchy.MatchId, "cap.detect", "1.0.0", "1.0.0",
			"foundational-facts", "pass", x.Start, x.End, 0.5, $$"""{"o":{{x.Ordinal}}}""")).ToList();
		await using var scope = f.Factory.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(records, ct);
		await uow.CommitAsync(ct);
	}

	// Six facts over two accepted results: three pages of two.
	private static async Task SeedFactsAsync(AgentOrchestrationFixture f, SeededRun seed, CancellationToken ct)
	{
		await InsertFactsAsync(f, seed, seed.ResultIds[0], ct, (0, 0, 100), (1, 0, 100), (2, 50, 150));
		await InsertFactsAsync(f, seed, seed.ResultIds[1], ct, (0, 0, 100), (1, 300_000, 300_100), (2, 0, 50));
	}

	private static async Task<HttpResponseMessage> MemberAsync(
		AgentOrchestrationFixture f, HttpMethod method, string path, object? body, string? idempotencyKey, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
		}

		if (idempotencyKey is not null)
		{
			request.Headers.Add("Idempotency-Key", idempotencyKey);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct, HttpStatusCode expected = HttpStatusCode.OK)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(expected, text);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<Attempt> StartAttemptAsync(AgentOrchestrationFixture f, Guid runId, CancellationToken ct)
	{
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId },
			Guid.NewGuid().ToString("N"), null, ct);
		var conversationId = (await ReadAsync(created, ct, HttpStatusCode.Created)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, null,
			created.Headers.ETag!.Tag, ct);
		await ReadAsync(added, ct);

		using var submitted = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = "q" },
			Guid.NewGuid().ToString("N"), null, ct);
		var invocationId = (await ReadAsync(submitted, ct, HttpStatusCode.Accepted)).GetProperty("invocationId").GetGuid();

		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var envelope = await ReadAsync(claim, ct, HttpStatusCode.Created);
		return new Attempt(envelope.GetProperty("attemptId").GetGuid(), envelope.GetProperty("fencingToken").GetString()!, token);
	}

	private static Task<HttpResponseMessage> CallAsync(AgentOrchestrationFixture f, Attempt a, object? arguments, CancellationToken ct) =>
		f.Workload.CallToolAsync(f.Anon, f.Now, a.Token, a.AttemptId, GetMatch, a.FencingToken, "call-" + Guid.NewGuid().ToString("N"), arguments, ct);

	private static async Task<JsonElement> MemberJsonAsync(AgentOrchestrationFixture f, string path, CancellationToken ct)
	{
		using var response = await f.Admin.Client.GetAsync(path, ct);
		return await ReadAsync(response, ct);
	}

	private static string FactsPath(Guid runId, string query) =>
		$"/api/v1/analysis/runs/{runId}/facts?capabilityId=cap.detect&capabilityVersion=1.0.0&schemaVersion=1.0.0&nodeKey=detect{query}";

	private static void ShouldMatch(JsonElement actual, JsonElement expected, string property)
	{
		var a = JsonNode.Parse(actual.GetProperty(property).GetRawText());
		var e = JsonNode.Parse(expected.GetProperty(property).GetRawText());
		JsonNode.DeepEquals(a, e).ShouldBeTrue($"{property}: {a?.ToJsonString()} != {e?.ToJsonString()}");
	}

	[Fact]
	public async Task OverviewAndFactPagesEqualTheMemberReadsAndNeverIncludeAnotherRun()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var seed = await SeedRunAsync(f, DateTimeOffset.UtcNow, ct);
		await SeedFactsAsync(f, seed, ct);

		// Another run of the same match with its own facts of the same capability and stream.
		var other = await SeedRunAsync(f, DateTimeOffset.UtcNow.AddHours(-1), ct);
		await InsertFactsAsync(f, other, other.ResultIds[0], ct, (0, 0, 100), (1, 10, 20));
		var otherResults = other.ResultIds.ToHashSet();

		var attempt = await StartAttemptAsync(f, seed.RunId, ct);

		using var overviewResponse = await CallAsync(f, attempt, null, ct);
		var overview = await ReadAsync(overviewResponse, ct);
		overview.GetProperty("outcome").GetString().ShouldBe("answered");
		overview.GetProperty("toolId").GetString().ShouldBe("get_match");
		var result = overview.GetProperty("result");

		var members = await MemberJsonAsync(f, $"/api/v1/analysis/runs/{seed.RunId}/fact-streams", ct);
		var matchRead = await MemberJsonAsync(f, $"/api/v1/matches/{f.Hierarchy.MatchId}", ct);
		result.GetProperty("run").GetProperty("runId").GetGuid().ShouldBe(seed.RunId);
		result.GetProperty("run").GetProperty("state").GetString().ShouldBe(members.GetProperty("run").GetProperty("state").GetString());
		result.GetProperty("run").GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
		result.GetProperty("match").GetProperty("matchId").GetGuid().ShouldBe(f.Hierarchy.MatchId);
		result.GetProperty("match").GetProperty("teamId").GetGuid().ShouldBe(matchRead.GetProperty("teamId").GetGuid());
		var streams = result.GetProperty("streams");
		streams.GetArrayLength().ShouldBe(members.GetProperty("items").GetArrayLength());
		for (var i = 0; i < streams.GetArrayLength(); i++)
		{
			var member = members.GetProperty("items")[i];
			var tool = streams[i];
			tool.GetProperty("nodeKey").GetString().ShouldBe(member.GetProperty("nodeKey").GetString());
			tool.GetProperty("factCount").GetInt32().ShouldBe(member.GetProperty("factCount").GetInt32());
			ShouldMatch(tool, member, "coverage");
			ShouldMatch(tool, member, "capability");
			ShouldMatch(tool, member, "provenance");
		}

		result.TryGetProperty("facts", out _).ShouldBeFalse();

		var keys = new List<(Guid, int)>();
		string? continuation = null;
		var pages = 0;
		do
		{
			var facts = new Dictionary<string, object>
			{
				["capabilityId"] = "cap.detect",
				["capabilityVersion"] = "1.0.0",
				["schemaVersion"] = "1.0.0",
				["nodeKey"] = "detect",
				["pageSize"] = 2,
			};
			if (continuation is not null)
			{
				facts["continuationToken"] = continuation;
			}

			using var pageResponse = await CallAsync(f, attempt, new { facts }, ct);
			var page = (await ReadAsync(pageResponse, ct)).GetProperty("result").GetProperty("facts");
			var member = await MemberJsonAsync(f, FactsPath(seed.RunId, "&pageSize=2" + (continuation is null ? "" : $"&continuationToken={continuation}")), ct);
			pages++;

			foreach (var property in new[] { "items", "coverage", "range", "continuationToken", "stream" })
			{
				ShouldMatch(page, member, property);
			}

			page.GetProperty("stream").GetProperty("provenance").GetProperty("workflow").GetProperty("id").GetString().ShouldBe("wf");
			page.GetProperty("coverage").GetArrayLength().ShouldBe(3);
			keys.AddRange(page.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("acceptedResultId").GetGuid(), i.GetProperty("ordinal").GetInt32())));
			continuation = page.GetProperty("continuationToken").ValueKind == JsonValueKind.String
				? page.GetProperty("continuationToken").GetString()
				: null;
		}
		while (continuation is not null);

		pages.ShouldBe(3);
		keys.Count.ShouldBe(6);
		keys.Distinct().Count().ShouldBe(6);
		keys.ShouldAllBe(k => !otherResults.Contains(k.Item1));

		using var rangeResponse = await CallAsync(
			f, attempt, new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", fromMs = 100, toMs = 300_000 } }, ct);
		var ranged = (await ReadAsync(rangeResponse, ct)).GetProperty("result").GetProperty("facts");
		var rangedMember = await MemberJsonAsync(f, FactsPath(seed.RunId, "&fromMs=100&toMs=300000"), ct);
		ranged.GetProperty("items").GetArrayLength().ShouldBe(1);
		ranged.GetProperty("items")[0].GetProperty("startMs").GetInt64().ShouldBe(50);
		foreach (var property in new[] { "items", "coverage", "range" })
		{
			ShouldMatch(ranged, rangedMember, property);
		}
	}

	[Fact]
	public async Task ASupersededBoundRunIsStillReadAndReportedAsNotCurrent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var older = await SeedRunAsync(f, DateTimeOffset.UtcNow.AddHours(-2), ct);
		await InsertFactsAsync(f, older, older.ResultIds[0], ct, (0, 0, 100), (1, 100, 200));
		var attempt = await StartAttemptAsync(f, older.RunId, ct);

		using var before = await CallAsync(f, attempt, null, ct);
		(await ReadAsync(before, ct)).GetProperty("result").GetProperty("run").GetProperty("isCurrent").GetBoolean().ShouldBeTrue();

		var newer = await SeedRunAsync(f, DateTimeOffset.UtcNow, ct);
		await InsertFactsAsync(f, newer, newer.ResultIds[0], ct, (0, 0, 100), (1, 100, 200), (2, 200, 300));
		await using (var connection = new NpgsqlConnection(f.Db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await connection.ExecuteAsync(new CommandDefinition(
				"""
				INSERT INTO socalytics.analysis_run_supersessions (run_id, match_id, superseding_run_id, superseded_at, previous_state)
				VALUES (@older, @match, @newer, now(), 'Completed')
				""",
				new { older = older.RunId, match = f.Hierarchy.MatchId, newer = newer.RunId }, cancellationToken: ct));
		}

		var arguments = new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect" } };
		using var after = await CallAsync(f, attempt, arguments, ct);
		var answer = await ReadAsync(after, ct);
		answer.GetProperty("outcome").GetString().ShouldBe("answered");
		var run = answer.GetProperty("result").GetProperty("run");
		run.GetProperty("runId").GetGuid().ShouldBe(older.RunId);
		run.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
		run.GetProperty("supersededBy").GetProperty("runId").GetGuid().ShouldBe(newer.RunId);

		var items = answer.GetProperty("result").GetProperty("facts").GetProperty("items").EnumerateArray().ToList();
		items.Count.ShouldBe(2);
		items.ShouldAllBe(i => i.GetProperty("acceptedResultId").GetGuid() == older.ResultIds[0]);
	}

	[Fact]
	public async Task AnUnknownCapabilityIsUnavailableAndInvalidSelectionsAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var seed = await SeedRunAsync(f, DateTimeOffset.UtcNow, ct);
		await SeedFactsAsync(f, seed, ct);
		var attempt = await StartAttemptAsync(f, seed.RunId, ct);

		object Selection(string nodeKey = "detect", string schemaVersion = "1.0.0") =>
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion, nodeKey } };

		foreach (var unknown in new[] { Selection("unused"), Selection(schemaVersion: "2.0.0") })
		{
			using var response = await CallAsync(f, attempt, unknown, ct);
			var answer = await ReadAsync(response, ct);
			answer.GetProperty("outcome").GetString().ShouldBe("unavailable");
			answer.GetProperty("result").GetProperty("reason").GetString().ShouldBe("capability-not-in-run");
		}

		var invalid = new object[]
		{
			new { facts = new { pageSize = 2 } },
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", pageSize = 0 } },
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", pageSize = 501 } },
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", fromMs = -1 } },
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", fromMs = 10, toMs = 10 } },
			new { facts = new { capabilityId = "cap.detect", capabilityVersion = "1.0.0", schemaVersion = "1.0.0", nodeKey = "detect", continuationToken = "abc" } },
			new { facts = new { capabilityId = "cap.detect", unexpected = 1 } },
			new { other = 1 },
		};
		foreach (var arguments in invalid)
		{
			using var response = await CallAsync(f, attempt, arguments, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(arguments));
		}
	}
}
