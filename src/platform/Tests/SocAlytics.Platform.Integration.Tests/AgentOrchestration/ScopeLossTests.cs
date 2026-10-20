using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class ScopeLossTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";

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

	private static async Task LoseScopeAsync(AgentOrchestrationFixture f, Setup s, string loss, CancellationToken ct)
	{
		if (loss == "role-removed")
		{
			await f.ScalarAsync(
				$"WITH d AS (DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM d", ct);
		}
		else
		{
			await f.ScalarAsync(
				$"WITH u AS (UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = '{s.OwnerId}' RETURNING 1) SELECT count(*) FROM u", ct);
		}
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, s.InvocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static Task<HttpResponseMessage> OperateAsync(
		AgentOrchestrationFixture f, Setup s, string operation, Guid attemptId, string fencingToken, CancellationToken ct)
	{
		var operationId = "op-" + Guid.NewGuid().ToString("N");
		return operation switch
		{
			"renewal" => f.Workload.SendAsync(
				f.Anon, f.Now, s.Token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/lease", new { fencingToken }, ct),
			"checkpoint" => f.Workload.CheckpointAsync(
				f.Anon, f.Now, s.Token, attemptId, new { operationId, fencingToken, kind = "progress", note = "note" }, ct),
			"completion" => f.Workload.CompleteAsync(
				f.Anon, f.Now, s.Token, attemptId, new { operationId, fencingToken, advice = "late advice", executionReferences = References }, ct),
			"failure" => f.Workload.FailAsync(
				f.Anon, f.Now, s.Token, attemptId, new { operationId, fencingToken, reasonCode = "model-error", retryable = true }, ct),
			_ => throw new ArgumentOutOfRangeException(nameof(operation)),
		};
	}

	private static async Task AssertScopeLostStaleAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			var body = await ReadAsync(response, ct);
			body.GetProperty("code").GetString().ShouldBe("agent-attempt-stale");
			body.GetProperty("disposition").GetString().ShouldBe("scope-lost");
		}
	}

	// The invocation failed with scope-lost, its only attempt was fenced, and nothing was retried or accepted.
	private static async Task AssertEndedAsync(AgentOrchestrationFixture f, Setup s, long attempts, CancellationToken ct)
	{
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{s.InvocationId}' AND state = 'failed' AND failure_reason = 'scope-lost' AND current_attempt_id IS NULL", ct))
			.ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{s.InvocationId}'", ct)).ShouldBe(attempts);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_accepted_advice WHERE invocation_id = '{s.InvocationId}'", ct)).ShouldBe(0);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{s.InvocationId}' AND kind = 'scope-lost'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", ct)).ShouldBe(1);
	}

	[Theory]
	[InlineData("role-removed")]
	[InlineData("deactivated")]
	public async Task LossBeforeTheClaimRefusesItAndFailsTheInvocation(string loss)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		await LoseScopeAsync(f, s, loss, ct);

		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, s.InvocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Conflict, await claim.Content.ReadAsStringAsync(ct));
		(await ReadAsync(claim, ct)).GetProperty("code").GetString().ShouldBe("agent-scope-lost");

		await AssertEndedAsync(f, s, 0, ct);

		using var again = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, s.InvocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		again.StatusCode.ShouldNotBe(HttpStatusCode.Created);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{s.InvocationId}'", ct)).ShouldBe(0);
	}

	[Theory]
	[InlineData("renewal", "role-removed")]
	[InlineData("checkpoint", "role-removed")]
	[InlineData("completion", "role-removed")]
	[InlineData("failure", "role-removed")]
	[InlineData("renewal", "deactivated")]
	[InlineData("checkpoint", "deactivated")]
	[InlineData("completion", "deactivated")]
	[InlineData("failure", "deactivated")]
	public async Task LossDuringAnActiveAttemptFencesItOnTheNextCall(string operation, string loss)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await ClaimAsync(f, s, ct);
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		var token = envelope.GetProperty("fencingToken").GetString()!;
		await LoseScopeAsync(f, s, loss, ct);

		await AssertScopeLostStaleAsync(await OperateAsync(f, s, operation, attemptId, token, ct), ct);

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attemptId}' AND state = 'fenced' AND end_reason = 'scope-lost'", ct))
			.ShouldBe(1);
		await AssertEndedAsync(f, s, 1, ct);

		foreach (var later in new[] { "renewal", "checkpoint", "completion", "failure" })
		{
			await AssertScopeLostStaleAsync(await OperateAsync(f, s, later, attemptId, token, ct), ct);
		}

		await AssertEndedAsync(f, s, 1, ct);
	}

	[Theory]
	[InlineData("role-removed")]
	[InlineData("deactivated")]
	public async Task TheMonitorFencesAnActiveAttemptWithoutAnyCall(string loss)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await ClaimAsync(f, s, ct);
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		var token = envelope.GetProperty("fencingToken").GetString()!;

		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			var unchanged = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			unchanged.ScopeLost.ShouldBe(0);
		}

		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attemptId}' AND state = 'active'", ct)).ShouldBe(1);

		await LoseScopeAsync(f, s, loss, ct);
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			result.ScopeLost.ShouldBe(1);
			result.Requeued.ShouldBe(0);
		}

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attemptId}' AND state = 'fenced' AND end_reason = 'scope-lost'", ct))
			.ShouldBe(1);
		await AssertEndedAsync(f, s, 1, ct);

		await AssertScopeLostStaleAsync(await OperateAsync(f, s, "completion", attemptId, token, ct), ct);
		await AssertScopeLostStaleAsync(await OperateAsync(f, s, "renewal", attemptId, token, ct), ct);
		await AssertEndedAsync(f, s, 1, ct);
	}
}
