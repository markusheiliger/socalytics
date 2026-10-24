using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// SC-001, SC-003, SC-005, SC-007: deletion of a member's own conversation.
public sealed class LifecycleDeleteTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "Which pressing triggers did the team miss?";

	private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? ETag);

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

	private static async Task<Reply> SendAsync(
		ApiSession session, HttpMethod method, string path, object? body, string? key, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		}

		if (key is not null)
		{
			request.Headers.Add("Idempotency-Key", key);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		using var response = await session.Client.SendAsync(request, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
		return new(response.StatusCode, json, response.Headers.ETag?.Tag);
	}

	private static Task<Reply> DeleteAsync(ApiSession s, Guid id, string? ifMatch, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Delete, $"/api/v1/agent-conversations/{id}", null, null, ifMatch, ct);

	private static Task<Reply> ArchiveAsync(ApiSession s, Guid id, string? ifMatch, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/archive", null, null, ifMatch, ct);

	private static Task<Reply> GetAsync(ApiSession s, string path, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Get, path, null, null, null, ct);

	private static Task<Reply> SubmitAsync(ApiSession s, Guid id, string key, string question, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/invocations", new { agentId = Tactics, question }, key, null, ct);

	private static async Task<IReadOnlyList<Guid>> ListAsync(ApiSession s, Guid matchId, string? state, CancellationToken ct)
	{
		var query = state is null ? string.Empty : $"?state={state}";
		var list = await GetAsync(s, $"/api/v1/matches/{matchId}/agent-conversations{query}", ct);
		list.Status.ShouldBe(HttpStatusCode.OK, list.Body.ToString());
		return [.. list.Body.GetProperty("conversations").EnumerateArray().Select(c => c.GetProperty("conversationId").GetGuid())];
	}

	private static async Task<(Guid Id, string ETag)> CreateAsync(
		AgentOrchestrationFixture f, ApiSession owner, Guid runId, CancellationToken ct)
	{
		var created = await SendAsync(
			owner, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId },
			Guid.NewGuid().ToString("N"), null, ct);
		created.Status.ShouldBe(HttpStatusCode.Created, created.Body.ToString());
		var id = created.Body.GetProperty("conversationId").GetGuid();
		var added = await SendAsync(
			owner, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/participants", new { agentId = Tactics }, null, created.ETag, ct);
		added.Status.ShouldBe(HttpStatusCode.OK, added.Body.ToString());
		return (id, added.ETag!);
	}

	private static async Task<(Guid Id, ApiSession Session)> MemberAsync(
		AgentOrchestrationFixture f, string name, string role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{id}', '{f.Hierarchy.TeamId}', '{role}', now(), '{id}')",
			ct);
		return (id, await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct));
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
		return JsonDocument.Parse(text).RootElement.Clone();
	}

	private static string Fencing(JsonElement envelope) => envelope.GetProperty("fencingToken").GetString()!;

	private static Guid AttemptId(JsonElement envelope) => envelope.GetProperty("attemptId").GetGuid();

	private static string NewOperation() => "op-" + Guid.NewGuid().ToString("N");

	private static Task<long> AuditAsync(AgentOrchestrationFixture f, string eventType, Guid conversationId, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}' AND resource_id = '{conversationId}'", ct);

	private static Task<long> TombstonesAsync(AgentOrchestrationFixture f, Guid id, CancellationToken ct) =>
		f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_conversation_tombstones WHERE conversation_id = '{id}'", ct);

	private static async Task AssertNotFoundAsync(ApiSession session, string path, CancellationToken ct)
	{
		var reply = await GetAsync(session, path, ct);
		reply.Status.ShouldBe(HttpStatusCode.NotFound, path);
		reply.Body.ToString().ShouldNotContain(Question);
	}

	private static async Task AssertEveryReadNotFoundAsync(
		AgentOrchestrationFixture f, Guid conversationId, IEnumerable<Guid> invocations, CancellationToken ct, params ApiSession[] readers)
	{
		foreach (var reader in readers)
		{
			await AssertNotFoundAsync(reader, $"/api/v1/agent-conversations/{conversationId}", ct);
			await AssertNotFoundAsync(reader, $"/api/v1/agent-conversations/{conversationId}/turns", ct);
			foreach (var invocation in invocations)
			{
				await AssertNotFoundAsync(reader, $"/api/v1/agent-invocations/{invocation}", ct);
				await AssertNotFoundAsync(reader, $"/api/v1/agent-invocations/{invocation}/lineage", ct);
			}

			(await ListAsync(reader, f.Hierarchy.MatchId, null, ct)).ShouldNotContain(conversationId);
			(await ListAsync(reader, f.Hierarchy.MatchId, "archived", ct)).ShouldNotContain(conversationId);
		}
	}

	private static void ShouldBeRejected(HttpResponseMessage response)
	{
		response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Conflict);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DeletingActiveAndArchivedConversationsHidesThemFromEveryReader(bool archiveFirst)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var other = await CreateAsync(f, f.Admin, run, ct);
		var submitted = await SubmitAsync(f.Admin, id, "k-1", Question, ct);
		submitted.Status.ShouldBe(HttpStatusCode.Accepted, submitted.Body.ToString());
		var invocation = submitted.Body.GetProperty("invocationId").GetGuid();
		var (_, coach) = await MemberAsync(f, "coach-1", "coach", ct);
		using var _coach = coach;

		if (archiveFirst)
		{
			var archived = await ArchiveAsync(f.Admin, id, etag, ct);
			archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());
			etag = archived.ETag!;
		}

		var deleted = await DeleteAsync(f.Admin, id, etag, ct);
		deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.Body.ToString());

		await AssertEveryReadNotFoundAsync(f, id, [invocation], ct, f.Admin, coach);
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, null, ct)).ShouldBe([other.Id]);
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{other.Id}", ct)).Status.ShouldBe(HttpStatusCode.OK);
		(await TombstonesAsync(f, id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.deleted", id, ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-conversation-purge' AND payload::text LIKE '%{id}%'", ct))
			.ShouldBe(1);
	}

	[Fact]
	public async Task DeleteSuppressesQueuedAndRunningInvocationsWithoutConsumingBudgetAndRejectsLateWorkloadOperations()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var queued = (await SubmitAsync(f.Admin, id, "k-queued", Question, ct)).Body.GetProperty("invocationId").GetGuid();
		var running = (await SubmitAsync(f.Admin, id, "k-running", Question + " Second.", ct)).Body.GetProperty("invocationId").GetGuid();
		var envelope = await ClaimAsync(f, token, running, ct);
		var attemptId = AttemptId(envelope);
		var fencing = Fencing(envelope);

		var usedBefore = await f.ScalarAsync($"SELECT coalesce(sum(attempts_used), 0) FROM socalytics.agent_invocations WHERE conversation_id = '{id}'", ct);
		var attemptsBefore = await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct);
		usedBefore.ShouldBe(1);

		var deleted = await DeleteAsync(f.Admin, id, etag, ct);
		deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.Body.ToString());

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE conversation_id = '{id}' AND state = 'lifecycle-suppressed' AND current_attempt_id IS NULL AND ended_at IS NOT NULL",
			ct)).ShouldBe(2);
		(await f.ScalarAsync($"SELECT coalesce(sum(attempts_used), 0) FROM socalytics.agent_invocations WHERE conversation_id = '{id}'", ct))
			.ShouldBe(usedBefore);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct)).ShouldBe(attemptsBefore);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_attempts WHERE id = '{attemptId}' AND state = 'fenced' AND end_reason = 'lifecycle-suppressed'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync($"SELECT invocations_suppressed FROM socalytics.agent_conversation_tombstones WHERE conversation_id = '{id}'", ct))
			.ShouldBe(2);

		using (var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, queued, NewOperation(), ct))
		{
			ShouldBeRejected(claim);
		}

		using (var again = await f.Workload.ClaimAsync(f.Anon, f.Now, token, running, NewOperation(), ct))
		{
			ShouldBeRejected(again);
		}

		using (var renewal = await f.Workload.SendAsync(
			f.Anon, f.Now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/lease", new { fencingToken = fencing }, ct))
		{
			ShouldBeRejected(renewal);
		}

		using (var checkpoint = await f.Workload.CheckpointAsync(
			f.Anon, f.Now, token, attemptId, new { operationId = NewOperation(), fencingToken = fencing, kind = "progress", note = "late" }, ct))
		{
			ShouldBeRejected(checkpoint);
		}

		using (var completion = await f.Workload.CompleteAsync(
			f.Anon, f.Now, token, attemptId,
			new { operationId = NewOperation(), fencingToken = fencing, advice = "Late advice.", executionReferences = References }, ct))
		{
			ShouldBeRejected(completion);
		}

		using (var failure = await f.Workload.FailAsync(
			f.Anon, f.Now, token, attemptId, new { operationId = NewOperation(), fencingToken = fencing, reasonCode = "model-error", retryable = true }, ct))
		{
			ShouldBeRejected(failure);
		}

		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_accepted_advice WHERE invocation_id IN ('{queued}', '{running}')", ct)).ShouldBe(0);
		(await f.ScalarAsync($"SELECT coalesce(sum(attempts_used), 0) FROM socalytics.agent_invocations WHERE conversation_id = '{id}'", ct))
			.ShouldBe(usedBefore);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_attempts", ct)).ShouldBe(attemptsBefore);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE conversation_id = '{id}' AND state = 'lifecycle-suppressed'", ct)).ShouldBe(2);
	}

	[Fact]
	public async Task ACompletionRacingTheTombstoneCreatesNoVisibleAdvice()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var invocation = (await SubmitAsync(f.Admin, id, "k-race", Question, ct)).Body.GetProperty("invocationId").GetGuid();
		var envelope = await ClaimAsync(f, token, invocation, ct);

		var completion = f.Workload.CompleteAsync(
			f.Anon, f.Now, token, AttemptId(envelope),
			new { operationId = NewOperation(), fencingToken = Fencing(envelope), advice = "Racing advice.", executionReferences = References }, ct);
		var deletion = DeleteAsync(f.Admin, id, etag, ct);
		using var completed = await completion;
		var deleted = await deletion;

		// Whichever commits second loses: a delete that lost to the completion is stale, and a completion after the tombstone is rejected.
		completed.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
		deleted.Status.ShouldBeOneOf(HttpStatusCode.NoContent, HttpStatusCode.PreconditionFailed);
		if (deleted.Status == HttpStatusCode.PreconditionFailed)
		{
			// The completion raced ahead; delete again with the current version.
			var current = await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct);
			current.Status.ShouldBe(HttpStatusCode.OK);
			var retry = await DeleteAsync(f.Admin, id, "\"" + current.Body.GetProperty("version").GetRawText() + "\"", ct);
			retry.Status.ShouldBeOneOf(HttpStatusCode.NoContent, HttpStatusCode.PreconditionFailed);
			if (retry.Status != HttpStatusCode.NoContent)
			{
				return;
			}
		}

		await AssertEveryReadNotFoundAsync(f, id, [invocation], ct, f.Admin);
		(await TombstonesAsync(f, id, ct)).ShouldBe(1);
		if (completed.StatusCode != HttpStatusCode.OK)
		{
			(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_accepted_advice WHERE invocation_id = '{invocation}'", ct)).ShouldBe(0);
		}
	}

	[Fact]
	public async Task ACompletionAfterTheTombstoneIsRejectedAndCreatesNoAdvice()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var invocation = (await SubmitAsync(f.Admin, id, "k-after", Question, ct)).Body.GetProperty("invocationId").GetGuid();
		var envelope = await ClaimAsync(f, token, invocation, ct);

		(await DeleteAsync(f.Admin, id, etag, ct)).Status.ShouldBe(HttpStatusCode.NoContent);

		using var completion = await f.Workload.CompleteAsync(
			f.Anon, f.Now, token, AttemptId(envelope),
			new { operationId = NewOperation(), fencingToken = Fencing(envelope), advice = "Too late.", executionReferences = References }, ct);
		ShouldBeRejected(completion);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_accepted_advice WHERE invocation_id = '{invocation}'", ct)).ShouldBe(0);
		await AssertEveryReadNotFoundAsync(f, id, [invocation], ct, f.Admin);
	}

	[Fact]
	public async Task ARepeatedDeleteIsASuccessOnlyForTheInitiatorStillInScope()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (coachId, coach) = await MemberAsync(f, "coach-1", "coach", ct);
		using var _coach = coach;
		using var viewer = (await MemberAsync(f, "viewer-1", "viewer", ct)).Session;
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var (coachConversation, coachEtag) = await CreateAsync(f, coach, run, ct);

		(await DeleteAsync(f.Admin, id, etag, ct)).Status.ShouldBe(HttpStatusCode.NoContent);

		foreach (var ifMatch in new[] { etag, "\"999\"" })
		{
			(await DeleteAsync(f.Admin, id, ifMatch, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
		}

		(await DeleteAsync(coach, id, etag, ct)).Status.ShouldBe(HttpStatusCode.NotFound);
		var never = await DeleteAsync(f.Admin, Guid.NewGuid(), etag, ct);
		never.Status.ShouldBe(HttpStatusCode.NotFound);
		(await TombstonesAsync(f, id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.deleted", id, ct)).ShouldBe(1);

		(await DeleteAsync(coach, coachConversation, coachEtag, ct)).Status.ShouldBe(HttpStatusCode.NoContent);
		(await DeleteAsync(f.Admin, coachConversation, coachEtag, ct)).Status.ShouldBe(HttpStatusCode.NotFound);
		(await DeleteAsync(coach, coachConversation, coachEtag, ct)).Status.ShouldBe(HttpStatusCode.NoContent);

		await f.ScalarAsync(
			$"WITH d AS (DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{coachId}' RETURNING 1) SELECT count(*) FROM d", ct);
		var lost = await DeleteAsync(coach, coachConversation, coachEtag, ct);
		lost.Status.ShouldBe(HttpStatusCode.NotFound, lost.Body.ToString());
		(await TombstonesAsync(f, coachConversation, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task StaleAndMissingVersionsAreRefusedAndViewersAndOthersAreTurnedAway()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		using var viewer = (await MemberAsync(f, "viewer-1", "viewer", ct)).Session;
		using var coach = (await MemberAsync(f, "coach-1", "coach", ct)).Session;

		var stale = await DeleteAsync(f.Admin, id, "\"999\"", ct);
		stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.Body.ToString());
		var missing = await DeleteAsync(f.Admin, id, null, ct);
		missing.Status.ShouldBe((HttpStatusCode)428, missing.Body.ToString());
		(await DeleteAsync(viewer, id, etag, ct)).Status.ShouldBe(HttpStatusCode.Forbidden);
		(await DeleteAsync(coach, id, etag, ct)).Status.ShouldBe(HttpStatusCode.NotFound);

		var read = await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct);
		read.Status.ShouldBe(HttpStatusCode.OK);
		read.Body.GetProperty("state").GetString().ShouldBe("active");
		(await TombstonesAsync(f, id, ct)).ShouldBe(0);
		(await AuditAsync(f, "agent.conversation.deleted", id, ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-conversation-purge'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task ConcurrentDeletesYieldOneTombstone()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DeleteAsync(f.Admin, id, etag, ct)));

		// A loser either finds the tombstone (repeat delete) or a conversation that is no longer there.
		results.ShouldAllBe(r => r.Status == HttpStatusCode.NoContent || r.Status == HttpStatusCode.NotFound);
		results.Count(r => r.Status == HttpStatusCode.NoContent).ShouldBeGreaterThanOrEqualTo(1);
		(await TombstonesAsync(f, id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.deleted", id, ct)).ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-conversation-purge' AND payload::text LIKE '%{id}%'", ct))
			.ShouldBe(1);
		await AssertEveryReadNotFoundAsync(f, id, [], ct, f.Admin);
	}
}
