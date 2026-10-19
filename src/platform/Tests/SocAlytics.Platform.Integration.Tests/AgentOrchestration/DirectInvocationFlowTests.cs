using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class DirectInvocationFlowTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Video = "specialist-video-analysis";

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

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		if (text.Length > 0 && text[0] != '{' && text[0] != '[')
		{
			throw new InvalidOperationException($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {text[..Math.Min(text.Length, 1500)]}");
		}

		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<HttpResponseMessage> MemberAsync(
		AgentOrchestrationFixture f, HttpMethod method, string path, object? body, string? idempotencyKey, string? ifMatch,
		string? ifNoneMatch, CancellationToken ct)
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

		if (ifNoneMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<Guid> CreateConversationWithParticipantsAsync(AgentOrchestrationFixture f, Guid runId, CancellationToken ct)
	{
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId },
			Guid.NewGuid().ToString("N"), null, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		var etag = created.Headers.ETag!.Tag;
		foreach (var agent in new[] { Tactics, Video })
		{
			using var added = await MemberAsync(
				f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = agent }, null, etag, null, ct);
			added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
			etag = added.Headers.ETag!.Tag;
		}

		return conversationId;
	}

	private static async Task<Guid> SubmitAsync(
		AgentOrchestrationFixture f, Guid conversationId, string agentId, string question, CancellationToken ct)
	{
		using var response = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId, question },
			Guid.NewGuid().ToString("N"), null, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
		return (await ReadAsync(response, ct)).GetProperty("invocationId").GetGuid();
	}

	private static async Task<(HttpStatusCode Status, JsonElement Body)> ClaimAsync(
		AgentOrchestrationFixture f, string token, Guid invocationId, string operationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, operationId, ct);
		return (response.StatusCode, await ReadAsync(response, ct));
	}

	private static object Completion(JsonElement envelope, string advice, string operationId) => new
	{
		operationId,
		fencingToken = envelope.GetProperty("fencingToken").GetString(),
		advice,
		executionReferences = References,
	};

	private static async Task CompleteAsync(
		AgentOrchestrationFixture f, string token, JsonElement envelope, string advice, CancellationToken ct)
	{
		using var response = await f.Workload.CompleteAsync(
			f.Anon, f.Now, token, envelope.GetProperty("attemptId").GetGuid(), Completion(envelope, advice, "complete-" + Guid.NewGuid().ToString("N")), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
	}

	// Submits, claims, and completes one invocation; time advances so the next submission is strictly later.
	private static async Task<Guid> RunToSuccessAsync(
		AgentOrchestrationFixture f, string token, Guid conversationId, string agentId, string question, string advice, CancellationToken ct)
	{
		var invocationId = await SubmitAsync(f, conversationId, agentId, question, ct);
		var (status, envelope) = await ClaimAsync(f, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		status.ShouldBe(HttpStatusCode.Created, envelope.ToString());
		await CompleteAsync(f, token, envelope, advice, ct);
		f.Factory.Time.Advance(TimeSpan.FromSeconds(1));
		return invocationId;
	}

	[Fact]
	public async Task ClaimGrantsAnEnvelopeWithBoundedHistoryOfAllParticipants()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct, s => s["AgentOrchestration:Execution:HistoryTurns"] = "2");
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var conversationId = await CreateConversationWithParticipantsAsync(f, runId, ct);

		await RunToSuccessAsync(f, token, conversationId, Tactics, "q1", "a1", ct);
		var second = await RunToSuccessAsync(f, token, conversationId, Video, "q2", "a2", ct);
		var third = await RunToSuccessAsync(f, token, conversationId, Tactics, "q3", "a3", ct);

		var fourth = await SubmitAsync(f, conversationId, Video, "q4", ct);
		var (status, envelope) = await ClaimAsync(f, token, fourth, "claim-fourth", ct);
		status.ShouldBe(HttpStatusCode.Created, envelope.ToString());

		var valid = f.Factory.Services.GetRequiredService<IContractValidator>()
			.Validate(ContractSchemaIds.AgentExecutionEnvelopeV1, envelope);
		valid.IsValid.ShouldBeTrue(string.Join(", ", valid.Errors.Select(e => $"{e.Field}:{e.Keyword}")));
		envelope.GetProperty("invocationId").GetGuid().ShouldBe(fourth);
		envelope.GetProperty("conversationId").GetGuid().ShouldBe(conversationId);
		envelope.GetProperty("agentId").GetString().ShouldBe(Video);
		envelope.GetProperty("question").GetString().ShouldBe("q4");

		var history = envelope.GetProperty("history").EnumerateArray().ToList();
		history.Count.ShouldBe(2);
		history.Select(h => h.GetProperty("invocationId").GetGuid()).ShouldBe([second, third], ignoreOrder: true);
		history.Select(h => h.GetProperty("agentId").GetString()).ShouldBe([Video, Tactics], ignoreOrder: true);
		history.Select(h => h.GetProperty("advice").GetString()).ShouldBe(["a2", "a3"], ignoreOrder: true);
	}

	[Fact]
	public async Task DuplicateClaimsAreRefusedAndRepeatedOperationsReturnTheSameGrant()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var conversationId = await CreateConversationWithParticipantsAsync(f, await f.SeedRunAsync(SeededRunState.Completed, ct: ct), ct);
		var invocationId = await SubmitAsync(f, conversationId, Tactics, "question", ct);

		var (firstStatus, first) = await ClaimAsync(f, token, invocationId, "claim-a", ct);
		firstStatus.ShouldBe(HttpStatusCode.Created, first.ToString());
		first.GetProperty("attemptNumber").GetInt32().ShouldBe(1);

		var (obsoleteStatus, obsolete) = await ClaimAsync(f, token, invocationId, "claim-b", ct);
		obsoleteStatus.ShouldBe(HttpStatusCode.Conflict);
		obsolete.GetProperty("code").GetString().ShouldBe("claim-obsolete");

		var (replayStatus, replay) = await ClaimAsync(f, token, invocationId, "claim-a", ct);
		replayStatus.ShouldBe(HttpStatusCode.OK);
		replay.GetProperty("attemptId").GetGuid().ShouldBe(first.GetProperty("attemptId").GetGuid());
		replay.GetProperty("fencingToken").GetString().ShouldBe(first.GetProperty("fencingToken").GetString());

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task CompletionSucceedsTheInvocationAndReadsShowItWithConditionalRequests()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var conversationId = await CreateConversationWithParticipantsAsync(f, await f.SeedRunAsync(SeededRunState.Completed, ct: ct), ct);
		var invocationId = await SubmitAsync(f, conversationId, Tactics, "What should we change?", ct);
		var invocationPath = $"/api/v1/agent-invocations/{invocationId}";
		var turnsPath = $"/api/v1/agent-conversations/{conversationId}/turns";

		using (var queued = await MemberAsync(f, HttpMethod.Get, invocationPath, null, null, null, null, ct))
		{
			queued.StatusCode.ShouldBe(HttpStatusCode.OK);
			(await ReadAsync(queued, ct)).GetProperty("state").GetString().ShouldBe("accepted/queued");
		}

		var (status, envelope) = await ClaimAsync(f, token, invocationId, "claim-1", ct);
		status.ShouldBe(HttpStatusCode.Created, envelope.ToString());
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		var completion = Completion(envelope, "Press higher.", "complete-1");
		using (var completed = await f.Workload.CompleteAsync(f.Anon, f.Now, token, attemptId, completion, ct))
		{
			completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));
			var body = await ReadAsync(completed, ct);
			body.GetProperty("invocationState").GetString().ShouldBe("succeeded");
			body.GetProperty("replayed").GetBoolean().ShouldBeFalse();
		}

		using (var replayed = await f.Workload.CompleteAsync(f.Anon, f.Now, token, attemptId, completion, ct))
		{
			(await ReadAsync(replayed, ct)).GetProperty("replayed").GetBoolean().ShouldBeTrue();
		}

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_accepted_advice", ct)).ShouldBe(1);

		string invocationEtag;
		using (var read = await MemberAsync(f, HttpMethod.Get, invocationPath, null, null, null, null, ct))
		{
			read.StatusCode.ShouldBe(HttpStatusCode.OK);
			var body = await ReadAsync(read, ct);
			body.GetProperty("state").GetString().ShouldBe("succeeded");
			body.GetProperty("advice").GetString().ShouldBe("Press higher.");
			invocationEtag = read.Headers.ETag!.Tag;
		}

		string turnsEtag;
		using (var turns = await MemberAsync(f, HttpMethod.Get, turnsPath, null, null, null, null, ct))
		{
			turns.StatusCode.ShouldBe(HttpStatusCode.OK);
			var turn = (await ReadAsync(turns, ct)).GetProperty("turns").EnumerateArray().Single();
			turn.GetProperty("invocationId").GetGuid().ShouldBe(invocationId);
			turn.GetProperty("state").GetString().ShouldBe("succeeded");
			turn.GetProperty("advice").GetString().ShouldBe("Press higher.");
			turnsEtag = turns.Headers.ETag!.Tag;
		}

		using var unchangedInvocation = await MemberAsync(f, HttpMethod.Get, invocationPath, null, null, null, invocationEtag, ct);
		unchangedInvocation.StatusCode.ShouldBe(HttpStatusCode.NotModified);
		using var unchangedTurns = await MemberAsync(f, HttpMethod.Get, turnsPath, null, null, null, turnsEtag, ct);
		unchangedTurns.StatusCode.ShouldBe(HttpStatusCode.NotModified);
	}

	[Fact]
	public async Task PollingCreatesNoInvocationOrAttempt()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var conversationId = await CreateConversationWithParticipantsAsync(f, await f.SeedRunAsync(SeededRunState.Completed, ct: ct), ct);
		var invocationId = await SubmitAsync(f, conversationId, Tactics, "question", ct);

		string? etag = null;
		for (var i = 0; i < 5; i++)
		{
			using var read = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, null, etag, ct);
			read.StatusCode.ShouldBe(i == 0 ? HttpStatusCode.OK : HttpStatusCode.NotModified);
			etag = read.Headers.ETag!.Tag;
			using var turns = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-conversations/{conversationId}/turns", null, null, null, null, ct);
			turns.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations", ct)).ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations WHERE state = 'queued'", ct)).ShouldBe(1);
	}
}
