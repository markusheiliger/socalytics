using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Application.AgentOrchestration.Tools;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

// FR-016 to FR-018; SC-004 over HTTP against PostgreSQL.
public sealed class ToolCallLineageTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string GetMatch = "get-match";
	private const string GetVideoClip = "get-video-clip";
	private const string FindTurnovers = "find-turnovers";

	private static readonly object References = new
	{
		runtime = "test-runtime",
		modelProvider = "test-provider",
		model = "test-model",
		deployment = "test-deployment",
		promptRef = "prompt-1",
		toolPolicyRef = "tools-1",
		safetyPolicyRef = "safety-1",
	};

	private static readonly string[] DetailKeys =
		["callId", "toolId", "outcome", "reason", "argumentsDigest", "resultDigest", "evidence", "producingCapability"];

	// The workers are off so that only the calls made by a test move state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private sealed record Setup(string Token, Guid OwnerId, Guid InvocationId, ApiSession Session);

	private sealed record Claimed(Guid AttemptId, string FencingToken);

	private sealed record Expected(string CallId, string Tool, string Outcome, string? Reason);

	private static async Task<Guid> SeedRunAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var digest = "sha-256:" + new string('a', 64);
		var run = new NewAnalysisRun(
			f.Hierarchy.TeamId, f.Hierarchy.MatchId, Guid.NewGuid(), DateTimeOffset.UtcNow, "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Completed, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", digest),
			[], []);
		await using var scope = f.Factory.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var id = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
		await uow.CommitAsync(ct);
		return id;
	}

	private static async Task<Setup> PrepareAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var owner = await TestMembers.SeedAsync(f.Db, "coach-1", cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{owner}', '{f.Hierarchy.TeamId}', 'coach', now(), '{owner}')",
			ct);
		var session = await ApiSession.SignInAsync(f.Factory, "coach-1", TestMembers.DefaultPassword, ct);
		var run = await SeedRunAsync(f, ct);

		using var created = await PostAsync(
			session, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = run }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("conversationId").GetGuid();
		using var added = await PostAsync(
			session, $"/api/v1/agent-conversations/{conversation}/participants", new { agentId = Tactics }, created.Headers.ETag!.ToString(), ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		using var submitted = await PostAsync(
			session, $"/api/v1/agent-conversations/{conversation}/invocations", new { agentId = Tactics, question = "What should we change?" }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocation = (await submitted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("invocationId").GetGuid();
		return new Setup(token, owner, invocation, session);
	}

	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return session.Client.SendAsync(request, ct);
	}

	private static async Task<Claimed> ClaimAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, s.InvocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
		var envelope = JsonDocument.Parse(text).RootElement;
		return new Claimed(envelope.GetProperty("attemptId").GetGuid(), envelope.GetProperty("fencingToken").GetString()!);
	}

	private static async Task<HttpStatusCode> CallAsync(
		AgentOrchestrationFixture f, Setup s, Claimed a, string segment, string callId, object? arguments, CancellationToken ct)
	{
		using var response = await f.Workload.CallToolAsync(f.Anon, f.Now, s.Token, a.AttemptId, segment, a.FencingToken, callId, arguments, ct);
		return response.StatusCode;
	}

	private static async Task<List<string>> QueryAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))];
	}

	private static async Task<List<JsonElement>> ToolEntriesAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		var rows = await QueryAsync(
			f,
			$"SELECT payload::text FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'tool-call' ORDER BY sequence",
			ct);
		return [.. rows.Select(r => JsonDocument.Parse(r).RootElement.Clone())];
	}

	private static void AssertEntries(List<JsonElement> entries, params Expected[] expected)
	{
		entries.Select(e => e.GetProperty("callId").GetString()).ShouldBe(expected.Select(e => e.CallId));
		for (var i = 0; i < expected.Length; i++)
		{
			var entry = entries[i];
			entry.GetProperty("toolId").GetString().ShouldBe(expected[i].Tool);
			entry.GetProperty("outcome").GetString().ShouldBe(expected[i].Outcome);
			if (expected[i].Reason is { } reason)
			{
				entry.GetProperty("reason").GetString().ShouldBe(reason);
			}

			entry.GetProperty("argumentsDigest").GetString()!.ShouldStartWith("sha-256:");
			var evidence = entry.GetProperty("evidence");
			evidence.GetProperty("facts").ValueKind.ShouldBe(JsonValueKind.Array);
			evidence.GetProperty("media").ValueKind.ShouldBe(JsonValueKind.Array);
			entry.EnumerateObject().Select(p => p.Name).ShouldAllBe(name => DetailKeys.Contains(name));
			var text = entry.GetRawText();
			text.ShouldNotContain("X-Amz-Signature");
			text.ShouldNotContain("\"url\"");
			text.ShouldNotContain("\"payload\"");
		}
	}

	[Fact]
	public async Task Every_call_outcome_has_exactly_one_entry_in_recorded_order()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		using var _ = s.Session;
		var attempt = await ClaimAsync(f, s, ct);
		var expected = new List<Expected>();

		async Task<HttpStatusCode> Call(string segment, string tool, string outcome, string? reason, object? arguments)
		{
			var callId = "call-" + Guid.NewGuid().ToString("N");
			expected.Add(new Expected(callId, tool, outcome, reason));
			return await CallAsync(f, s, attempt, segment, callId, arguments, ct);
		}

		(await Call(GetMatch, "get_match", "answered", null, null)).ShouldBe(HttpStatusCode.OK);
		(await Call(FindTurnovers, "find_turnovers", "unavailable", "no-accepted-producer", null)).ShouldBe(HttpStatusCode.OK);
		(await Call(GetVideoClip, "get_video_clip", "not-found", null, new { fact = new { acceptedResultId = Guid.NewGuid(), ordinal = 0 } }))
			.ShouldBe(HttpStatusCode.NotFound);
		(await Call(GetMatch, "get_match", "invalid", null, new { other = 1 })).ShouldBe(HttpStatusCode.BadRequest);

		// Fill the call budget with reservations that already have no entry of their own to stay out of the order under test.
		var reserved = await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_tool_calls WHERE attempt_id = '{attempt.AttemptId}'", ct);
		await f.ScalarAsync(
			$"""
			WITH i AS (
				INSERT INTO socalytics.agent_tool_calls (attempt_id, call_id, invocation_id, tool_id, arguments_digest, is_clip_resolution, reserved_at)
				SELECT '{attempt.AttemptId}', 'seed-' || g, '{s.InvocationId}', 'get_match', 'sha-256:{new string('a', 64)}', false, now() + interval '1 hour'
				FROM generate_series(1, {50 - reserved}) g RETURNING 1)
			SELECT count(*) FROM i
			""", ct);
		(await Call(GetMatch, "get_match", "limit-reached", null, null)).ShouldBe(HttpStatusCode.TooManyRequests);

		await f.ScalarAsync($"WITH d AS (DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM d", ct);
		(await Call(GetMatch, "get_match", "scope-lost", null, null)).ShouldBe(HttpStatusCode.Conflict);

		AssertEntries(await ToolEntriesAsync(f, s, ct), [.. expected]);
	}

	[Fact]
	public async Task A_tool_outside_the_policy_and_an_interrupted_reservation_each_have_one_entry()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		using var _ = s.Session;

		// Pins are immutable, so the test repoints this invocation's pin to a registered policy that permits only the clip tool.
		await using (var connection = new NpgsqlConnection(f.Db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await connection.ExecuteAsync(new CommandDefinition(
				$$"""
				INSERT INTO socalytics.agent_catalog_artifacts (kind, artifact_id, artifact_version, version_major, digest, agent_group, document, registered_by, registered_at)
				VALUES ('tool-policy', 'clip-only-tools', '0.1.0', 0, 'sha-256:{{new string('c', 64)}}', 'specialist',
				        '{"schemaVersion":"1.0.0","kind":"tool-policy","id":"clip-only-tools","version":"0.1.0","tools":["get_video_clip"]}'::jsonb, 'test', now());
				ALTER TABLE socalytics.agent_invocation_pins DISABLE TRIGGER trg_agent_invocation_pins_immutable;
				UPDATE socalytics.agent_invocation_pins SET artifact_id = 'clip-only-tools', artifact_version = '0.1.0', digest = 'sha-256:{{new string('c', 64)}}'
				WHERE invocation_id = '{{s.InvocationId}}' AND kind = 'tool-policy';
				ALTER TABLE socalytics.agent_invocation_pins ENABLE TRIGGER trg_agent_invocation_pins_immutable;
				""",
				cancellationToken: ct));
		}

		var attempt = await ClaimAsync(f, s, ct);
		(await CallAsync(f, s, attempt, GetMatch, "call-denied", null, ct)).ShouldBe(HttpStatusCode.Forbidden);

		// A process that stopped after the reservation leaves no entry until the attempt ended and the monitor ran.
		await f.ScalarAsync(
			$"""
			WITH i AS (
				INSERT INTO socalytics.agent_tool_calls (attempt_id, call_id, invocation_id, tool_id, arguments_digest, is_clip_resolution, reserved_at)
				VALUES ('{attempt.AttemptId}', 'call-interrupted', '{s.InvocationId}', 'get_video_clip', 'sha-256:{new string('a', 64)}', true, now())
				RETURNING 1)
			SELECT count(*) FROM i
			""", ct);
		(await ToolEntriesAsync(f, s, ct)).Count.ShouldBe(1);

		using (var done = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, attempt.AttemptId,
			new { operationId = "op-" + Guid.NewGuid().ToString("N"), fencingToken = attempt.FencingToken, advice = "done", executionReferences = References }, ct))
		{
			done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(ct));
		}

		for (var round = 0; round < 2; round++)
		{
			await using var scope = f.Factory.Services.CreateAsyncScope();
			var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			result.InterruptedCalls.ShouldBe(round == 0 ? 1 : 0);
		}

		AssertEntries(
			await ToolEntriesAsync(f, s, ct),
			new Expected("call-denied", "get_match", "not-permitted", null),
			new Expected("call-interrupted", "get_video_clip", "failed", "interrupted"));
	}

	[Fact]
	public async Task The_owner_reads_the_entries_other_members_get_not_found_and_reading_calls_no_tool()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		using var _ = s.Session;
		var attempt = await ClaimAsync(f, s, ct);

		var first = "call-" + Guid.NewGuid().ToString("N");
		var second = "call-" + Guid.NewGuid().ToString("N");
		var third = "call-" + Guid.NewGuid().ToString("N");
		(await CallAsync(f, s, attempt, GetMatch, first, null, ct)).ShouldBe(HttpStatusCode.OK);
		(await CallAsync(f, s, attempt, GetMatch, second, null, ct)).ShouldBe(HttpStatusCode.OK);
		(await CallAsync(f, s, attempt, FindTurnovers, third, null, ct)).ShouldBe(HttpStatusCode.OK);

		var path = $"/api/v1/agent-invocations/{s.InvocationId}/lineage";
		var calls = await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_tool_calls", ct);
		var entries = await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_lineage_entries", ct);

		using var read = await s.Session.SendAsync(HttpMethod.Get, path, null, ct);
		var text = await read.Content.ReadAsStringAsync(ct);
		read.StatusCode.ShouldBe(HttpStatusCode.OK, text);
		var toolEntries = JsonDocument.Parse(text).RootElement.GetProperty("entries").EnumerateArray()
			.Where(e => e.GetProperty("kind").GetString() == "tool-call")
			.Select(e => e.GetProperty("details"))
			.ToList();
		AssertEntries(
			toolEntries,
			new Expected(first, "get_match", "answered", null),
			new Expected(second, "get_match", "answered", null),
			new Expected(third, "find_turnovers", "unavailable", "no-accepted-producer"));

		// Equal results have equal digests, and the unavailable answer names its producing capability.
		toolEntries[0].GetProperty("resultDigest").GetString().ShouldBe(toolEntries[1].GetProperty("resultDigest").GetString());
		toolEntries[0].GetProperty("argumentsDigest").GetString().ShouldBe(toolEntries[1].GetProperty("argumentsDigest").GetString());
		toolEntries[2].GetProperty("producingCapability").GetString().ShouldBe("possession-and-transitions");

		await TestMembers.SeedAsync(f.Db, "other-1", cancellationToken: ct);
		using var other = await ApiSession.SignInAsync(f.Factory, "other-1", TestMembers.DefaultPassword, ct);
		using var hidden = await other.SendAsync(HttpMethod.Get, path, null, ct);
		hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		(await hidden.Content.ReadAsStringAsync(ct)).ShouldNotContain("tool-call");

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_tool_calls", ct)).ShouldBe(calls);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_lineage_entries", ct)).ShouldBe(entries);
	}

	[Fact]
	public void Result_digests_exclude_links_and_are_stable_for_equal_results()
	{
		static JsonNode Result(string url, string expiresAt, int ordinal) => JsonNode.Parse(
			$$$"""
			{"requested":{"fromMs":0},"parts":[{"state":"available","media":{"mediaDigest":"sha-256:{{{new string('d', 64)}}}","url":"{{{url}}}","expiresAt":"{{{expiresAt}}}"}}],"ordinal":{{{ordinal}}}}
			""")!;

		var a = ToolCallLineage.ResultDigest(Result("https://storage.example/a?X-Amz-Signature=1", "2026-01-01T00:00:00Z", 1));
		var b = ToolCallLineage.ResultDigest(Result("https://storage.example/b?X-Amz-Signature=2", "2026-01-01T00:05:00Z", 1));
		var c = ToolCallLineage.ResultDigest(Result("https://storage.example/a?X-Amz-Signature=1", "2026-01-01T00:00:00Z", 2));

		a.ShouldBe(b);
		a.ShouldNotBe(c);
		a.ShouldStartWith("sha-256:");
	}
}
