using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

// FR-004 to FR-009, FR-018a, FR-018b; SC-002, SC-003 over HTTP against PostgreSQL.
public sealed class ToolCallAuthorizationTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string GetMatch = "get-match";
	private const string GetVideoClip = "get-video-clip";
	private const string RefusedEvent = "agent.tool.call.refused";
	private const string ScopeLostEvent = "agent.tool.call.scope-lost";

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

	// The workers are off so that only the calls made by a test move state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private sealed record Setup(string Token, Guid OwnerId, Guid InvocationId);

	private sealed record Claimed(Guid AttemptId, string FencingToken);

	private static async Task<Setup> PrepareAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var owner = await TestMembers.SeedAsync(f.Db, "coach-1", cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{owner}', '{f.Hierarchy.TeamId}', 'coach', now(), '{owner}')",
			ct);
		using var session = await ApiSession.SignInAsync(f.Factory, "coach-1", TestMembers.DefaultPassword, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);

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
		return new Setup(token, owner, invocation);
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

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<Claimed> ClaimAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, s.InvocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		var envelope = await ReadAsync(response, ct);
		return new Claimed(envelope.GetProperty("attemptId").GetGuid(), envelope.GetProperty("fencingToken").GetString()!);
	}

	private static Task<HttpResponseMessage> CallAsync(
		AgentOrchestrationFixture f, Setup s, Claimed a, string segment, CancellationToken ct, string? callId = null, string? fencingToken = null, object? arguments = null) =>
		f.Workload.CallToolAsync(
			f.Anon, f.Now, s.Token, a.AttemptId, segment, fencingToken ?? a.FencingToken, callId ?? "call-" + Guid.NewGuid().ToString("N"), arguments, ct);

	private static async Task<List<string>> QueryAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))];
	}

	private static Task<long> RefusedAsync(AgentOrchestrationFixture f, string reason, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{RefusedEvent}' AND details->>'reasonCode' = '{reason}'", ct);

	private static Task<long> ToolLineageAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'tool-call'", ct);

	private static Task<long> ToolCallsAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct) =>
		f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_tool_calls WHERE invocation_id = '{s.InvocationId}'", ct);

	private static async Task AssertStaleAsync(HttpResponseMessage response, string disposition, CancellationToken ct)
	{
		using (response)
		{
			var body = await ReadAsync(response, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body.GetRawText());
			body.GetProperty("code").GetString().ShouldBe("agent-attempt-stale");
			body.GetProperty("disposition").GetString().ShouldBe(disposition);
			body.GetRawText().ShouldNotContain("facts");
		}
	}

	[Fact]
	public async Task Member_sessions_manager_tokens_and_anonymous_callers_get_401_and_an_audit_event()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var (manager, managerToken) = await f.ActivateManagerAsync(ct);
		using var _ = manager;
		var path = TestAgentWorkload.ToolPath(Guid.NewGuid(), GetMatch);
		var body = new { callId = "call-1", fencingToken = "1", arguments = new { } };

		using var anonymous = await f.Anon.PostAsJsonAsync(path, body, ct);
		anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using var member = await f.Admin.SendAsync(HttpMethod.Post, path, body, ct);
		member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using var managerRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		managerRequest.Headers.TryAddWithoutValidation("Authorization", "DPoP " + managerToken);
		managerRequest.Headers.Add("DPoP", manager.DPoPProof(f.Now, "POST", TestAgentWorkload.Origin + path, managerToken));
		using var managerResponse = await f.Anon.SendAsync(managerRequest, ct);
		managerResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await RefusedAsync(f, "caller-not-workload", ct)).ShouldBe(3);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_tool_calls", ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_lineage_entries WHERE kind = 'tool-call'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task Stale_attempts_read_nothing_and_write_no_lineage_entry()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var first = await ClaimAsync(f, s, ct);

		async Task AssertNothingWrittenAsync(long refused)
		{
			(await ToolCallsAsync(f, s, ct)).ShouldBe(0);
			(await ToolLineageAsync(f, s, ct)).ShouldBe(0);
			(await RefusedAsync(f, "attempt-stale", ct)).ShouldBe(refused);
		}

		// A wrong token is fenced.
		var wrong = (long.Parse(first.FencingToken) + 1).ToString();
		await AssertStaleAsync(await CallAsync(f, s, first, GetMatch, ct, fencingToken: wrong), "attempt-fenced", ct);
		await AssertStaleAsync(await CallAsync(f, s, first, GetVideoClip, ct, fencingToken: wrong), "attempt-fenced", ct);
		await AssertNothingWrittenAsync(2);

		// The lease expired before the monitor ran.
		f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
		await AssertStaleAsync(await CallAsync(f, s, first, GetMatch, ct), "lease-expired", ct);
		await AssertNothingWrittenAsync(3);

		// The monitor replaced the attempt, so the old one is not current.
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			result.Requeued.ShouldBe(1);
		}

		var second = await ClaimAsync(f, s, ct);
		await AssertStaleAsync(await CallAsync(f, s, first, GetMatch, ct), "attempt-not-current", ct);
		await AssertNothingWrittenAsync(4);

		// The invocation ended.
		using (var done = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, second.AttemptId,
			new { operationId = "op-" + Guid.NewGuid().ToString("N"), fencingToken = second.FencingToken, advice = "done", executionReferences = References }, ct))
		{
			done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(ct));
		}

		await AssertStaleAsync(await CallAsync(f, s, second, GetMatch, ct), "invocation-ended", ct);
		await AssertStaleAsync(await CallAsync(f, s, second, GetVideoClip, ct), "invocation-ended", ct);
		await AssertNothingWrittenAsync(6);
	}

	[Fact]
	public async Task A_tool_outside_the_pinned_policy_answers_403_with_lineage_and_audit_and_no_fencing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);

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
		using var response = await CallAsync(f, s, attempt, GetMatch, ct);
		var body = await ReadAsync(response, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body.GetRawText());
		body.GetProperty("code").GetString().ShouldBe("agent-tool-not-permitted");

		var lineage = await QueryAsync(
			f, $"SELECT payload::text FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'tool-call'", ct);
		using var payload = JsonDocument.Parse(lineage.ShouldHaveSingleItem());
		payload.RootElement.GetProperty("outcome").GetString().ShouldBe("not-permitted");
		(await RefusedAsync(f, "not-permitted", ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attempt.AttemptId}' AND state = 'active'", ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{s.InvocationId}' AND state = 'running'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task A_reused_call_identifier_answers_409_and_records_only_an_audit_event()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var attempt = await ClaimAsync(f, s, ct);

		using var first = await CallAsync(f, s, attempt, GetMatch, ct, callId: "call-reused");
		first.StatusCode.ShouldNotBe(HttpStatusCode.Conflict);
		var lineageBefore = await ToolLineageAsync(f, s, ct);

		using var again = await CallAsync(f, s, attempt, GetMatch, ct, callId: "call-reused");
		var body = await ReadAsync(again, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.Conflict, body.GetRawText());
		body.GetProperty("code").GetString().ShouldBe("agent-tool-call-id-reused");

		(await RefusedAsync(f, "call-id-reused", ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_tool_calls WHERE attempt_id = '{attempt.AttemptId}' AND call_id = 'call-reused'", ct)).ShouldBe(1);
		(await ToolLineageAsync(f, s, ct)).ShouldBe(lineageBefore);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attempt.AttemptId}' AND state = 'active'", ct)).ShouldBe(1);
	}

	private static Task SeedReservationsAsync(AgentOrchestrationFixture f, Setup s, Claimed a, int count, string toolId, bool clip, CancellationToken ct) =>
		f.ScalarAsync(
			$"""
			WITH i AS (
				INSERT INTO socalytics.agent_tool_calls (attempt_id, call_id, invocation_id, tool_id, arguments_digest, is_clip_resolution, reserved_at)
				SELECT '{a.AttemptId}', 'seed-' || g, '{s.InvocationId}', '{toolId}', 'sha-256:{new string('a', 64)}', {(clip ? "true" : "false")}, now()
				FROM generate_series(1, {count}) g RETURNING 1)
			SELECT count(*) FROM i
			""", ct);

	[Fact]
	public async Task The_51st_call_answers_429_without_fencing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var attempt = await ClaimAsync(f, s, ct);
		await SeedReservationsAsync(f, s, attempt, 50, "get_match", false, ct);

		using var response = await CallAsync(f, s, attempt, GetMatch, ct);
		var body = await ReadAsync(response, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body.GetRawText());
		body.GetProperty("code").GetString().ShouldBe("agent-tool-call-limit-reached");
		body.GetProperty("limit").GetString().ShouldBe("calls");

		(await RefusedAsync(f, "limit-reached", ct)).ShouldBe(1);
		(await ToolLineageAsync(f, s, ct)).ShouldBe(1);
		(await ToolCallsAsync(f, s, ct)).ShouldBe(50);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attempt.AttemptId}' AND state = 'active'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task The_11th_clip_resolution_answers_429_while_other_tools_stay_available()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var attempt = await ClaimAsync(f, s, ct);
		await SeedReservationsAsync(f, s, attempt, 10, "get_video_clip", true, ct);

		using var response = await CallAsync(f, s, attempt, GetVideoClip, ct);
		var body = await ReadAsync(response, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body.GetRawText());
		body.GetProperty("code").GetString().ShouldBe("agent-tool-call-limit-reached");
		body.GetProperty("limit").GetString().ShouldBe("clip-resolutions");

		(await RefusedAsync(f, "limit-reached", ct)).ShouldBe(1);
		(await ToolLineageAsync(f, s, ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attempt.AttemptId}' AND state = 'active'", ct)).ShouldBe(1);

		using var other = await CallAsync(f, s, attempt, GetMatch, ct);
		other.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
		other.StatusCode.ShouldNotBe(HttpStatusCode.Conflict);
	}

	[Theory]
	[InlineData("role-removed")]
	[InlineData("deactivated")]
	[InlineData("viewer-only")]
	public async Task Losing_the_initiating_scope_fences_the_attempt_and_fails_the_invocation_in_the_same_transaction(string loss)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var attempt = await ClaimAsync(f, s, ct);

		switch (loss)
		{
			case "role-removed":
				await f.ScalarAsync(
					$"WITH d AS (DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM d", ct);
				break;
			case "deactivated":
				await f.ScalarAsync(
					$"WITH u AS (UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM u", ct);
				break;
			default:
				await f.ScalarAsync(
					$"WITH u AS (UPDATE socalytics.team_role_assignment SET role = 'viewer' WHERE member_account_id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM u", ct);
				break;
		}

		await AssertStaleAsync(await CallAsync(f, s, attempt, GetMatch, ct), "scope-lost", ct);

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attempt.AttemptId}' AND state = 'fenced' AND end_reason = 'scope-lost'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{s.InvocationId}' AND state = 'failed' AND failure_reason = 'scope-lost' AND current_attempt_id IS NULL", ct))
			.ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'scope-lost'", ct)).ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{ScopeLostEvent}'", ct)).ShouldBe(1);
		var lineage = await QueryAsync(
			f, $"SELECT payload::text FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'tool-call'", ct);
		using (var payload = JsonDocument.Parse(lineage.ShouldHaveSingleItem()))
		{
			payload.RootElement.GetProperty("outcome").GetString().ShouldBe("scope-lost");
		}

		// Every later call is stale and records no further lineage entry.
		foreach (var segment in new[] { GetMatch, GetVideoClip, GetMatch })
		{
			await AssertStaleAsync(await CallAsync(f, s, attempt, segment, ct), "scope-lost", ct);
		}

		(await ToolLineageAsync(f, s, ct)).ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{ScopeLostEvent}'", ct)).ShouldBe(1);
	}

	private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

	private static MeterListener Listen(List<Measurement> sink)
	{
		var listener = new MeterListener
		{
			InstrumentPublished = (instrument, l) =>
			{
				if (instrument.Meter.Name == AgentToolMetrics.MeterName)
				{
					l.EnableMeasurementEvents(instrument);
				}
			},
		};
		listener.SetMeasurementEventCallback<long>((i, v, t, _) =>
		{
			lock (sink)
			{
				sink.Add(new Measurement(i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value)));
			}
		});
		listener.Start();
		return listener;
	}

	[Fact]
	public async Task Concurrent_calls_each_get_one_lineage_entry_in_recorded_order_and_the_counter_carries_no_arguments()
	{
		var ct = TestContext.Current.CancellationToken;
		const int calls = 8;
		const string marker = "MARKER-7d41";
		var measurements = new List<Measurement>();
		using var listener = Listen(measurements);
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var attempt = await ClaimAsync(f, s, ct);

		var statuses = await Task.WhenAll(Enumerable.Range(0, calls).Select(async n =>
		{
			using var response = await CallAsync(
				f, s, attempt, GetVideoClip, ct, callId: $"call-{marker}-{n}",
				arguments: new { fact = new { acceptedResultId = Guid.NewGuid(), ordinal = 0 } });
			return response.StatusCode;
		}));
		statuses.ShouldAllBe(status => status == HttpStatusCode.NotFound);

		var rows = await QueryAsync(
			f,
			$"SELECT sequence::text || '|' || (payload->>'callId') FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'tool-call' ORDER BY sequence",
			ct);
		rows.Count.ShouldBe(calls);
		var sequences = rows.Select(r => long.Parse(r.Split('|')[0])).ToList();
		sequences.Distinct().Count().ShouldBe(calls);
		sequences.ShouldBe([.. sequences.Order()]);
		rows.Select(r => r.Split('|')[1]).Distinct().Count().ShouldBe(calls);
		var reserved = await QueryAsync(
			f, $"SELECT call_id FROM socalytics.agent_tool_calls WHERE invocation_id = '{s.InvocationId}' ORDER BY reserved_at, call_id", ct);
		reserved.Count.ShouldBe(calls);

		List<Measurement> own;
		lock (measurements)
		{
			own = [.. measurements.Where(m => m.Instrument == "agent_tool_calls_total")];
		}

		own.Count.ShouldBeGreaterThanOrEqualTo(calls);
		own.ShouldAllBe(m => m.Tags.Keys.Order().SequenceEqual(new[] { "outcome", "tool" }));
		foreach (var m in own)
		{
			m.Tags.Values.ShouldAllBe(v => !Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!.Contains(marker, StringComparison.Ordinal));
		}

		own.Count(m => (string?)m.Tags["tool"] == "get_video_clip" && (string?)m.Tags["outcome"] == "not-found").ShouldBeGreaterThanOrEqualTo(calls);
	}
}
