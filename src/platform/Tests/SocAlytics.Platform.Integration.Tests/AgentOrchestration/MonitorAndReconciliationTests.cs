using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// The fixture's mutable clock drives leases and deadlines (lease 2 s, deadline 10 s, republish bound 5 s, 3 attempts).
public sealed class MonitorAndReconciliationTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "Which pressing triggers did the team miss in the first half?";

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

	private static Task<AgentOrchestrationFixture> StartAsync(PostgresContainerFixture postgres, bool workers, CancellationToken ct) =>
		AgentOrchestrationFixture.StartAsync(postgres, ct, s => s["Analysis:Workers:Enabled"] = workers ? "true" : "false");

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

	private static async Task<HttpResponseMessage> MemberPostAsync(
		AgentOrchestrationFixture f, string path, object body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<Guid> SubmitAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await MemberPostAsync(
			f, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberPostAsync(
			f, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		using var submitted = await MemberPostAsync(
			f, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = Question }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		return (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static Task<HttpResponseMessage> CompleteAsync(
		AgentOrchestrationFixture f, string token, JsonElement envelope, CancellationToken ct) =>
		f.Workload.CompleteAsync(
			f.Anon, f.Now, token, envelope.GetProperty("attemptId").GetGuid(),
			new
			{
				operationId = "complete-" + Guid.NewGuid().ToString("N"),
				fencingToken = envelope.GetProperty("fencingToken").GetString(),
				advice = "Press higher.",
				executionReferences = References,
			}, ct);

	private static async Task<T> InScopeAsync<T>(AgentOrchestrationFixture f, Func<IServiceProvider, Task<T>> action)
	{
		await using var scope = f.Factory.Services.CreateAsyncScope();
		return await action(scope.ServiceProvider);
	}

	private static Task<AgentMonitorResult> MonitorAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		InScopeAsync(f, sp => sp.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct));

	private static Task<int> ReconcileAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		InScopeAsync(f, sp => sp.GetRequiredService<ReconcileAgentWorkHandler>().HandleAsync(ct));

	private static Task<long> ReadyMessagesAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", ct);

	[Fact]
	public async Task AttemptWithoutRenewalsExpiresAndANewAttemptCompletesTheInvocation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, workers: true, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var invocationId = await SubmitAsync(f, ct);
		var first = await ClaimAsync(f, token, invocationId, ct);

		// Past one lease, the running monitor ends the attempt within one monitor interval.
		f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
		var deadline = DateTime.UtcNow.AddSeconds(20);
		while (await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'expired' AND end_reason = 'lease-expired'", ct) == 0)
		{
			(DateTime.UtcNow < deadline).ShouldBeTrue("The monitor did not expire the attempt in time.");
			await Task.Delay(200, ct);
		}

		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocationId}' AND state = 'queued'", ct)).ShouldBe(1);

		var second = await ClaimAsync(f, token, invocationId, ct);
		second.GetProperty("attemptNumber").GetInt32().ShouldBe(2);
		second.GetProperty("attemptId").GetGuid().ShouldNotBe(first.GetProperty("attemptId").GetGuid());
		using var completed = await CompleteAsync(f, token, second, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));
		(await ReadAsync(completed, ct)).GetProperty("invocationState").GetString().ShouldBe("succeeded");

		// The expired attempt's late completion changes nothing.
		using var late = await CompleteAsync(f, token, first, ct);
		late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await ReadAsync(late, ct)).GetProperty("code").GetString().ShouldBe("agent-attempt-stale");
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_accepted_advice", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task AttemptThatReachesItsDeadlineIsEndedLikeAnExpiredLease()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, workers: false, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var invocationId = await SubmitAsync(f, ct);
		var envelope = await ClaimAsync(f, token, invocationId, ct);

		// Renewals keep the lease alive but never extend it past the 10 s deadline.
		for (var i = 0; i < 6; i++)
		{
			f.Factory.Time.Advance(TimeSpan.FromSeconds(1.5));
			using var renewed = await f.Workload.SendAsync(
				f.Anon, f.Now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{envelope.GetProperty("attemptId").GetGuid()}/lease",
				new { fencingToken = envelope.GetProperty("fencingToken").GetString() }, ct);
			renewed.StatusCode.ShouldBe(HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync(ct));
		}

		(await MonitorAsync(f, ct)).Expired.ShouldBe(0);
		f.Factory.Time.Advance(TimeSpan.FromSeconds(1.5));
		var result = await MonitorAsync(f, ct);

		result.Expired.ShouldBe(1);
		result.Requeued.ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'expired' AND end_reason = 'deadline-reached'", ct)).ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'deadline-reached'", ct)).ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocationId}' AND state = 'queued'", ct)).ShouldBe(1);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(2);
	}

	[Fact]
	public async Task ThirdExpiryFailsTheInvocationWithAttemptsExhausted()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, workers: false, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var invocationId = await SubmitAsync(f, ct);

		for (var attempt = 1; attempt <= 3; attempt++)
		{
			var envelope = await ClaimAsync(f, token, invocationId, ct);
			envelope.GetProperty("attemptNumber").GetInt32().ShouldBe(attempt);
			f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
			var result = await MonitorAsync(f, ct);
			result.Expired.ShouldBe(1);
			result.Exhausted.ShouldBe(attempt == 3 ? 1 : 0);
			result.Requeued.ShouldBe(attempt == 3 ? 0 : 1);
		}

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocationId}' AND state = 'failed' AND failure_reason = 'attempts-exhausted'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'exhausted'", ct)).ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'expired'", ct)).ShouldBe(3);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(3);

		// Nothing is left to expire and nothing can be claimed.
		(await MonitorAsync(f, ct)).Expired.ShouldBe(0);
		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-after-end", ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task LostNotificationIsRepublishedWithinTheBoundAndDuplicatesNeverCreateASecondActiveAttempt()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, workers: false, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var invocationId = await SubmitAsync(f, ct);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(1);

		// The notification is lost before it reaches the queue.
		await f.ScalarAsync("WITH d AS (DELETE FROM socalytics.outbox_messages RETURNING 1) SELECT count(*) FROM d", ct);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(0);

		f.Factory.Time.Advance(TimeSpan.FromSeconds(4));
		(await ReconcileAsync(f, ct)).ShouldBe(0);
		f.Factory.Time.Advance(TimeSpan.FromSeconds(2));
		(await ReconcileAsync(f, ct)).ShouldBe(1);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(1);

		// Repeating reconciliation does not republish again within the bound.
		(await ReconcileAsync(f, ct)).ShouldBe(0);
		(await ReadyMessagesAsync(f, ct)).ShouldBe(1);

		// A duplicate notification leads to claim attempts that create one active attempt only.
		await ClaimAsync(f, token, invocationId, ct);
		using var duplicate = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-duplicate", ct);
		duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state IN ('claimed', 'active')", ct)).ShouldBe(1);

		// A claimed invocation is no longer republished.
		f.Factory.Time.Advance(TimeSpan.FromSeconds(1));
		(await ReconcileAsync(f, ct)).ShouldBe(0);

		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT payload::text FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var payloads = 0;
		while (await reader.ReadAsync(ct))
		{
			var text = reader.GetString(0);
			text.ShouldNotContain(Question);
			var payload = JsonDocument.Parse(text).RootElement;
			payload.TryGetProperty("question", out _).ShouldBeFalse();
			payload.TryGetProperty("advice", out _).ShouldBeFalse();
			payloads++;
		}

		payloads.ShouldBe(1);
	}
}
