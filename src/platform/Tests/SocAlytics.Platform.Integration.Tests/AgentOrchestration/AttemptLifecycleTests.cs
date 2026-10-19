using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class AttemptLifecycleTests(PostgresContainerFixture postgres)
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

	// The workers are off so that only the handlers called by a test move time-dependent state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<HttpResponseMessage> MemberAsync(
		AgentOrchestrationFixture f, HttpMethod method, string path, object? body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
			request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private sealed record Setup(string Token, Guid ConversationId);

	private static async Task<Setup> PrepareAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics },
			created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		return new Setup(token, conversationId);
	}

	private static async Task<Guid> SubmitAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		using var response = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{s.ConversationId}/invocations",
			new { agentId = Tactics, question = "What should we change?" }, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
		return (await ReadAsync(response, ct)).GetProperty("invocationId").GetGuid();
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, Setup s, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static Guid AttemptId(JsonElement envelope) => envelope.GetProperty("attemptId").GetGuid();

	private static string FencingToken(JsonElement envelope) => envelope.GetProperty("fencingToken").GetString()!;

	private static string NewOperationId() => "op-" + Guid.NewGuid().ToString("N");

	private static Task<HttpResponseMessage> RenewAsync(
		AgentOrchestrationFixture f, Setup s, Guid attemptId, string fencingToken, CancellationToken ct) =>
		f.Workload.SendAsync(
			f.Anon, f.Now, s.Token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/lease", new { fencingToken }, ct);

	private static object CheckpointBody(string token, string note, string? operationId = null) =>
		new { operationId = operationId ?? NewOperationId(), fencingToken = token, kind = "progress", note };

	private static object CompletionBody(string token, string advice, string? operationId = null) =>
		new { operationId = operationId ?? NewOperationId(), fencingToken = token, advice, executionReferences = References };

	private static object FailureBody(string token, bool retryable, string? operationId = null) =>
		new { operationId = operationId ?? NewOperationId(), fencingToken = token, reasonCode = "model-error", retryable };

	private static async Task FailAttemptAsync(
		AgentOrchestrationFixture f, Setup s, JsonElement envelope, bool retryable, string expectedState, CancellationToken ct)
	{
		using var response = await f.Workload.FailAsync(f.Anon, f.Now, s.Token, AttemptId(envelope), FailureBody(FencingToken(envelope), retryable), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		(await ReadAsync(response, ct)).GetProperty("invocationState").GetString().ShouldBe(expectedState);
	}

	private static async Task AssertStaleAsync(HttpResponseMessage response, string disposition, CancellationToken ct)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			var body = await ReadAsync(response, ct);
			body.GetProperty("code").GetString().ShouldBe("agent-attempt-stale");
			body.GetProperty("disposition").GetString().ShouldBe(disposition);
		}
	}

	private static async Task<string> SnapshotAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var rows = await f.ScalarAsync(
			"""
			SELECT (SELECT count(*) FROM socalytics.agent_lineage_entries)
			     + (SELECT count(*) FROM socalytics.agent_accepted_advice)
			     + (SELECT count(*) FROM socalytics.agent_workload_operations)
			     + (SELECT count(*) FROM socalytics.outbox_messages)
			     + (SELECT coalesce(sum(version), 0) FROM socalytics.agent_attempts)
			     + (SELECT coalesce(sum(version), 0) FROM socalytics.agent_invocations)
			""", ct);
		var lease = await f.ScalarAsync(
			"SELECT coalesce((extract(epoch FROM max(lease_expires_at)) * 1000)::bigint, 0) FROM socalytics.agent_attempts", ct);
		return $"{rows}/{lease}";
	}

	private static async Task<string> InvocationFieldAsync(
		AgentOrchestrationFixture f, Guid invocationId, string field, CancellationToken ct)
	{
		using var read = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, ct);
		var value = (await ReadAsync(read, ct)).GetProperty(field);
		return value.ValueKind == JsonValueKind.Null ? "" : value.ToString();
	}

	private static Task<long> ReadyMessagesAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", ct);

	[Fact]
	public async Task RenewalsExtendTheLeaseButNeverPastTheDeadline()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await ClaimAsync(f, s, await SubmitAsync(f, s, ct), ct);
		var attemptId = AttemptId(envelope);
		var firstLease = envelope.GetProperty("leaseExpiresAt").GetDateTimeOffset();
		var deadline = envelope.GetProperty("deadlineAt").GetDateTimeOffset();

		f.Factory.Time.Advance(TimeSpan.FromSeconds(1));
		using (var first = await RenewAsync(f, s, attemptId, FencingToken(envelope), ct))
		{
			first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(ct));
			(await ReadAsync(first, ct)).GetProperty("leaseExpiresAt").GetDateTimeOffset().ShouldBeGreaterThan(firstLease);
		}

		var last = firstLease;
		for (var i = 0; i < 8; i++)
		{
			f.Factory.Time.Advance(TimeSpan.FromSeconds(1));
			using var renewed = await RenewAsync(f, s, attemptId, FencingToken(envelope), ct);
			renewed.StatusCode.ShouldBe(HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync(ct));
			last = (await ReadAsync(renewed, ct)).GetProperty("leaseExpiresAt").GetDateTimeOffset();
			last.ShouldBeLessThanOrEqualTo(deadline);
		}

		last.ShouldBe(deadline);
	}

	[Fact]
	public async Task CheckpointsAppearInOrderAndRepeatsReturnTheOriginalOutcome()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var invocationId = await SubmitAsync(f, s, ct);
		var envelope = await ClaimAsync(f, s, invocationId, ct);
		var attemptId = AttemptId(envelope);

		var sequences = new List<int>();
		foreach (var note in new[] { "first", "second", "third" })
		{
			using var response = await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, CheckpointBody(FencingToken(envelope), note), ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			sequences.Add((await ReadAsync(response, ct)).GetProperty("sequence").GetInt32());
		}

		sequences[1].ShouldBe(sequences[0] + 1);
		sequences[2].ShouldBe(sequences[1] + 1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'checkpoint'", ct)).ShouldBe(3);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'checkpoint' AND payload ->> 'note' = 'second' AND sequence = {sequences[1]}",
			ct)).ShouldBe(1);

		var repeated = CheckpointBody(FencingToken(envelope), "repeat", "op-fixed-checkpoint");
		using (var original = await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, repeated, ct))
		{
			(await ReadAsync(original, ct)).GetProperty("replayed").GetBoolean().ShouldBeFalse();
		}

		var before = await SnapshotAsync(f, ct);
		using (var replay = await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, repeated, ct))
		{
			replay.StatusCode.ShouldBe(HttpStatusCode.OK);
			var body = await ReadAsync(replay, ct);
			body.GetProperty("replayed").GetBoolean().ShouldBeTrue();
			body.GetProperty("sequence").GetInt32().ShouldBe(sequences[2] + 1);
		}

		using var reused = await f.Workload.CheckpointAsync(
			f.Anon, f.Now, s.Token, attemptId, CheckpointBody(FencingToken(envelope), "other content", "op-fixed-checkpoint"), ct);
		reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await ReadAsync(reused, ct)).GetProperty("code").GetString().ShouldBe("operation-id-reused");
		(await SnapshotAsync(f, ct)).ShouldBe(before);
	}

	[Fact]
	public async Task RetryableFailuresRequeueWithANewNotificationThenFailWithAttemptsExhausted()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var invocationId = await SubmitAsync(f, s, ct);
		var published = await ReadyMessagesAsync(f, ct);

		for (var attempt = 1; attempt <= 2; attempt++)
		{
			var envelope = await ClaimAsync(f, s, invocationId, ct);
			envelope.GetProperty("attemptNumber").GetInt32().ShouldBe(attempt);
			await FailAttemptAsync(f, s, envelope, retryable: true, "queued", ct);
			(await ReadyMessagesAsync(f, ct)).ShouldBe(published + attempt);
			(await InvocationFieldAsync(f, invocationId, "state", ct)).ShouldBe("accepted/queued");
		}

		var last = await ClaimAsync(f, s, invocationId, ct);
		last.GetProperty("attemptNumber").GetInt32().ShouldBe(3);
		await FailAttemptAsync(f, s, last, retryable: true, "failed", ct);

		(await InvocationFieldAsync(f, invocationId, "state", ct)).ShouldBe("failed");
		(await InvocationFieldAsync(f, invocationId, "failureReason", ct)).ShouldBe("attempts-exhausted");
		(await ReadyMessagesAsync(f, ct)).ShouldBe(published + 2);
	}

	[Fact]
	public async Task NonRetryableFailuresFailWithExecutionFailed()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var invocationId = await SubmitAsync(f, s, ct);
		var published = await ReadyMessagesAsync(f, ct);

		await FailAttemptAsync(f, s, await ClaimAsync(f, s, invocationId, ct), retryable: false, "failed", ct);

		(await InvocationFieldAsync(f, invocationId, "failureReason", ct)).ShouldBe("execution-failed");
		(await ReadyMessagesAsync(f, ct)).ShouldBe(published);
	}

	[Fact]
	public async Task StaleOperationsAreRefusedWithTheirDispositionAndChangeNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);

		// A wrong token is fenced, and nothing changes.
		var invocationId = await SubmitAsync(f, s, ct);
		var envelope = await ClaimAsync(f, s, invocationId, ct);
		var attemptId = AttemptId(envelope);
		var token = FencingToken(envelope);
		var wrong = (long.Parse(token) + 1).ToString();
		var before = await SnapshotAsync(f, ct);
		await AssertStaleAsync(await RenewAsync(f, s, attemptId, wrong, ct), "attempt-fenced", ct);
		await AssertStaleAsync(await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, CheckpointBody(wrong, "x"), ct), "attempt-fenced", ct);
		await AssertStaleAsync(await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, attemptId, CompletionBody(wrong, "x"), ct), "attempt-fenced", ct);
		await AssertStaleAsync(await f.Workload.FailAsync(f.Anon, f.Now, s.Token, attemptId, FailureBody(wrong, true), ct), "attempt-fenced", ct);
		(await SnapshotAsync(f, ct)).ShouldBe(before);

		// After the lease expired and before the monitor ran, the attempt is still current but expired.
		f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
		before = await SnapshotAsync(f, ct);
		await AssertStaleAsync(await RenewAsync(f, s, attemptId, token, ct), "lease-expired", ct);
		await AssertStaleAsync(await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, CheckpointBody(token, "x"), ct), "lease-expired", ct);
		await AssertStaleAsync(await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, attemptId, CompletionBody(token, "x"), ct), "lease-expired", ct);
		await AssertStaleAsync(await f.Workload.FailAsync(f.Anon, f.Now, s.Token, attemptId, FailureBody(token, true), ct), "lease-expired", ct);
		(await SnapshotAsync(f, ct)).ShouldBe(before);

		// After the monitor replaced the attempt, the old one is not current.
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			result.Expired.ShouldBe(1);
			result.Requeued.ShouldBe(1);
		}

		var second = await ClaimAsync(f, s, invocationId, ct);
		before = await SnapshotAsync(f, ct);
		await AssertStaleAsync(await RenewAsync(f, s, attemptId, token, ct), "attempt-not-current", ct);
		await AssertStaleAsync(await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, attemptId, CheckpointBody(token, "x"), ct), "attempt-not-current", ct);
		await AssertStaleAsync(await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, attemptId, CompletionBody(token, "x"), ct), "attempt-not-current", ct);
		await AssertStaleAsync(await f.Workload.FailAsync(f.Anon, f.Now, s.Token, attemptId, FailureBody(token, true), ct), "attempt-not-current", ct);
		(await SnapshotAsync(f, ct)).ShouldBe(before);

		// After the invocation ended, every operation of its last attempt is refused.
		var secondId = AttemptId(second);
		var secondToken = FencingToken(second);
		using (var done = await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, secondId, CompletionBody(secondToken, "done"), ct))
		{
			done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(ct));
		}

		before = await SnapshotAsync(f, ct);
		await AssertStaleAsync(await RenewAsync(f, s, secondId, secondToken, ct), "invocation-ended", ct);
		await AssertStaleAsync(await f.Workload.CheckpointAsync(f.Anon, f.Now, s.Token, secondId, CheckpointBody(secondToken, "x"), ct), "invocation-ended", ct);
		await AssertStaleAsync(await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, secondId, CompletionBody(secondToken, "again"), ct), "invocation-ended", ct);
		await AssertStaleAsync(await f.Workload.FailAsync(f.Anon, f.Now, s.Token, secondId, FailureBody(secondToken, true), ct), "invocation-ended", ct);
		(await SnapshotAsync(f, ct)).ShouldBe(before);
		(await InvocationFieldAsync(f, invocationId, "advice", ct)).ShouldBe("done");
	}

	[Fact]
	public async Task RepeatedOperationsReturnTheOriginalOutcomeAndOtherContentIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);

		var failing = await ClaimAsync(f, s, await SubmitAsync(f, s, ct), ct);
		var failure = FailureBody(FencingToken(failing), false, "op-fixed-failure");
		using (var original = await f.Workload.FailAsync(f.Anon, f.Now, s.Token, AttemptId(failing), failure, ct))
		{
			original.StatusCode.ShouldBe(HttpStatusCode.OK, await original.Content.ReadAsStringAsync(ct));
			(await ReadAsync(original, ct)).GetProperty("replayed").GetBoolean().ShouldBeFalse();
		}

		var before = await SnapshotAsync(f, ct);
		using (var replay = await f.Workload.FailAsync(f.Anon, f.Now, s.Token, AttemptId(failing), failure, ct))
		{
			replay.StatusCode.ShouldBe(HttpStatusCode.OK);
			var body = await ReadAsync(replay, ct);
			body.GetProperty("replayed").GetBoolean().ShouldBeTrue();
			body.GetProperty("invocationState").GetString().ShouldBe("failed");
		}

		using (var reused = await f.Workload.FailAsync(
			f.Anon, f.Now, s.Token, AttemptId(failing), FailureBody(FencingToken(failing), true, "op-fixed-failure"), ct))
		{
			reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await ReadAsync(reused, ct)).GetProperty("code").GetString().ShouldBe("operation-id-reused");
		}

		(await SnapshotAsync(f, ct)).ShouldBe(before);

		var completing = await ClaimAsync(f, s, await SubmitAsync(f, s, ct), ct);
		var completion = CompletionBody(FencingToken(completing), "Press higher.", "op-fixed-completion");
		using (var original = await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, AttemptId(completing), completion, ct))
		{
			original.StatusCode.ShouldBe(HttpStatusCode.OK, await original.Content.ReadAsStringAsync(ct));
		}

		before = await SnapshotAsync(f, ct);
		using (var replay = await f.Workload.CompleteAsync(f.Anon, f.Now, s.Token, AttemptId(completing), completion, ct))
		{
			replay.StatusCode.ShouldBe(HttpStatusCode.OK);
			(await ReadAsync(replay, ct)).GetProperty("replayed").GetBoolean().ShouldBeTrue();
		}

		using (var reused = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, AttemptId(completing), CompletionBody(FencingToken(completing), "Other advice.", "op-fixed-completion"), ct))
		{
			reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await ReadAsync(reused, ct)).GetProperty("code").GetString().ShouldBe("operation-id-reused");
		}

		(await SnapshotAsync(f, ct)).ShouldBe(before);

		var claimed = await SubmitAsync(f, s, ct);
		using var first = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, claimed, "op-fixed-claim", ct);
		first.StatusCode.ShouldBe(HttpStatusCode.Created);
		using var other = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, await SubmitAsync(f, s, ct), "op-fixed-claim", ct);
		other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await ReadAsync(other, ct)).GetProperty("code").GetString().ShouldBe("operation-id-reused");
	}

	[Fact]
	public async Task ConcurrentClaimsCreateOneActiveAttempt()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var invocationId = await SubmitAsync(f, s, ct);

		var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
		{
			using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, invocationId, $"claim-concurrent-{i}", ct);
			return (response.StatusCode, Body: await ReadAsync(response, ct));
		}));

		responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
		responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
		responses.Where(r => r.StatusCode == HttpStatusCode.Conflict)
			.ShouldAllBe(r => r.Body.GetProperty("code").GetString() == "claim-obsolete");
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct)).ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'active'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ACompletionRacingTheMonitorsExpiryLeavesExactlyOneOutcome()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var invocationId = await SubmitAsync(f, s, ct);
		var envelope = await ClaimAsync(f, s, invocationId, ct);

		// The clock stands exactly at the lease expiry, so both sides contend for the same attempt.
		f.Factory.Time.Set(envelope.GetProperty("leaseExpiresAt").GetDateTimeOffset());
		var completion = Task.Run(async () =>
		{
			using var response = await f.Workload.CompleteAsync(
				f.Anon, f.Now, s.Token, AttemptId(envelope), CompletionBody(FencingToken(envelope), "late"), ct);
			return response.StatusCode;
		}, ct);
		var monitor = Task.Run(async () =>
		{
			await using var scope = f.Factory.Services.CreateAsyncScope();
			return await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
		}, ct);
		var status = await completion;
		var expired = (await monitor).Expired;

		var advice = await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_accepted_advice", ct);
		var expiredAttempts = await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'expired'", ct);
		(advice + expiredAttempts).ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts WHERE state = 'active'", ct)).ShouldBe(0);
		expired.ShouldBe((int)expiredAttempts);
		status.ShouldBe(advice == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict);
	}
}
