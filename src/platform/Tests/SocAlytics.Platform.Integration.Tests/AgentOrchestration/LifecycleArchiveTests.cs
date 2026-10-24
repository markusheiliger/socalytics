using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// SC-007, SC-005: archive and unarchive of a member's own conversation.
public sealed class LifecycleArchiveTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "Which pressing triggers did the team miss?";
	private const string ArchivedCode = "conversation-archived";

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

	private static Task<Reply> ArchiveAsync(ApiSession s, Guid id, string? ifMatch, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/archive", null, null, ifMatch, ct);

	private static Task<Reply> UnarchiveAsync(ApiSession s, Guid id, string? ifMatch, CancellationToken ct) =>
		SendAsync(s, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/unarchive", null, null, ifMatch, ct);

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

	private static async Task<ApiSession> MemberAsync(AgentOrchestrationFixture f, string name, string role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{id}', '{f.Hierarchy.TeamId}', '{role}', now(), '{id}')",
			ct);
		return await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct);
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
		return JsonDocument.Parse(text).RootElement.Clone();
	}

	private static async Task CompleteAsync(
		AgentOrchestrationFixture f, string token, JsonElement envelope, string advice, CancellationToken ct)
	{
		var report = new
		{
			operationId = "complete-" + Guid.NewGuid().ToString("N"),
			fencingToken = envelope.GetProperty("fencingToken").GetString(),
			advice,
			executionReferences = References,
		};
		using var response = await f.Workload.CompleteAsync(f.Anon, f.Now, token, envelope.GetProperty("attemptId").GetGuid(), report, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
	}

	private static Task<long> AuditAsync(AgentOrchestrationFixture f, string eventType, Guid conversationId, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}' AND resource_id = '{conversationId}'", ct);

	[Fact]
	public async Task ArchiveAndUnarchiveRoundTripKeepsHistoryAttributionAndLineage()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var other = await CreateAsync(f, f.Admin, run, ct);

		var submitted = await SubmitAsync(f.Admin, id, "k-1", Question, ct);
		submitted.Status.ShouldBe(HttpStatusCode.Accepted, submitted.Body.ToString());
		var invocation = submitted.Body.GetProperty("invocationId").GetGuid();
		await CompleteAsync(f, token, await ClaimAsync(f, token, invocation, ct), "Press higher.", ct);

		var turnsPath = $"/api/v1/agent-conversations/{id}/turns";
		var lineagePath = $"/api/v1/agent-invocations/{invocation}/lineage";
		var turnsBefore = (await GetAsync(f.Admin, turnsPath, ct)).Body.GetRawText();
		var lineageBefore = (await GetAsync(f.Admin, lineagePath, ct)).Body.GetRawText();
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, null, ct)).ShouldBe([id, other.Id], ignoreOrder: true);

		var archived = await ArchiveAsync(f.Admin, id, etag, ct);
		archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());
		archived.Body.GetProperty("state").GetString().ShouldBe("archived");
		archived.ETag.ShouldNotBe(etag);
		archived.Body.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("agentId").GetString()).ShouldBe([Tactics]);

		(await ListAsync(f.Admin, f.Hierarchy.MatchId, null, ct)).ShouldBe([other.Id]);
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, "active", ct)).ShouldBe([other.Id]);
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, "archived", ct)).ShouldBe([id]);

		var read = await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct);
		read.Status.ShouldBe(HttpStatusCode.OK, read.Body.ToString());
		read.Body.GetProperty("state").GetString().ShouldBe("archived");
		var turnsArchived = await GetAsync(f.Admin, turnsPath, ct);
		turnsArchived.Status.ShouldBe(HttpStatusCode.OK);
		turnsArchived.Body.GetRawText().ShouldBe(turnsBefore);
		var turn = turnsArchived.Body.GetProperty("turns").EnumerateArray().Single();
		turn.GetProperty("agentId").GetString().ShouldBe(Tactics);
		turn.GetProperty("advice").GetString().ShouldBe("Press higher.");
		(await GetAsync(f.Admin, lineagePath, ct)).Body.GetRawText().ShouldBe(lineageBefore);

		var unarchived = await UnarchiveAsync(f.Admin, id, archived.ETag, ct);
		unarchived.Status.ShouldBe(HttpStatusCode.OK, unarchived.Body.ToString());
		unarchived.Body.GetProperty("state").GetString().ShouldBe("active");
		unarchived.ETag.ShouldNotBe(archived.ETag);
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, null, ct)).ShouldBe([id, other.Id], ignoreOrder: true);
		(await ListAsync(f.Admin, f.Hierarchy.MatchId, "archived", ct)).ShouldBeEmpty();
		(await GetAsync(f.Admin, turnsPath, ct)).Body.GetRawText().ShouldBe(turnsBefore);
		(await GetAsync(f.Admin, lineagePath, ct)).Body.GetRawText().ShouldBe(lineageBefore);

		var again = await SubmitAsync(f.Admin, id, "k-2", Question + " Again.", ct);
		again.Status.ShouldBe(HttpStatusCode.Accepted, again.Body.ToString());
		(await AuditAsync(f, "agent.conversation.archived", id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.unarchived", id, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ArchivedConversationsRefuseQuestionsAndSpecialists()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		var archived = await ArchiveAsync(f.Admin, id, etag, ct);
		archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());

		var submit = await SubmitAsync(f.Admin, id, "k-archived", Question, ct);
		submit.Status.ShouldBe(HttpStatusCode.Conflict, submit.Body.ToString());
		submit.Body.GetProperty("code").GetString().ShouldBe(ArchivedCode);

		var add = await SendAsync(
			f.Admin, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/participants",
			new { agentId = "specialist-video-analysis" }, null, archived.ETag, ct);
		add.Status.ShouldBe(HttpStatusCode.Conflict, add.Body.ToString());
		add.Body.GetProperty("code").GetString().ShouldBe(ArchivedCode);

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations", ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", ct)).ShouldBe(0);
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct)).Body.GetProperty("participants").GetArrayLength().ShouldBe(1);
	}

	[Fact]
	public async Task AnInFlightInvocationFinishesOnAnArchivedConversation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var queued = await SubmitAsync(f.Admin, id, "k-queued", Question, ct);
		queued.Status.ShouldBe(HttpStatusCode.Accepted, queued.Body.ToString());
		var queuedInvocation = queued.Body.GetProperty("invocationId").GetGuid();
		var running = await SubmitAsync(f.Admin, id, "k-running", Question + " Second.", ct);
		var runningInvocation = running.Body.GetProperty("invocationId").GetGuid();
		var envelope = await ClaimAsync(f, token, runningInvocation, ct);

		var archived = await ArchiveAsync(f.Admin, id, etag, ct);
		archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());

		await CompleteAsync(f, token, envelope, "Finished while archived.", ct);
		var finished = await GetAsync(f.Admin, $"/api/v1/agent-invocations/{runningInvocation}", ct);
		finished.Body.GetProperty("state").GetString().ShouldBe("succeeded");
		finished.Body.GetProperty("advice").GetString().ShouldBe("Finished while archived.");

		var queuedRead = await GetAsync(f.Admin, $"/api/v1/agent-invocations/{queuedInvocation}", ct);
		queuedRead.Body.GetProperty("state").GetString().ShouldBe("accepted/queued");
		var claim = await ClaimAsync(f, token, queuedInvocation, ct);
		await CompleteAsync(f, token, claim, "Queued one finished too.", ct);

		var turns = (await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}/turns", ct)).Body.GetProperty("turns").EnumerateArray().ToList();
		turns.Count.ShouldBe(2);
		turns.ShouldAllBe(t => t.GetProperty("state").GetString() == "succeeded");
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct)).Body.GetProperty("state").GetString().ShouldBe("archived");
	}

	[Fact]
	public async Task StaleAndMissingVersionsAreRefusedAndChangeNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var stale = await ArchiveAsync(f.Admin, id, "\"999\"", ct);
		stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.Body.ToString());
		var missing = await ArchiveAsync(f.Admin, id, null, ct);
		missing.Status.ShouldBe((HttpStatusCode)428, missing.Body.ToString());
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct)).Body.GetProperty("state").GetString().ShouldBe("active");

		var archived = await ArchiveAsync(f.Admin, id, etag, ct);
		archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());

		var staleUnarchive = await UnarchiveAsync(f.Admin, id, etag, ct);
		staleUnarchive.Status.ShouldBe(HttpStatusCode.PreconditionFailed, staleUnarchive.Body.ToString());
		var missingUnarchive = await UnarchiveAsync(f.Admin, id, null, ct);
		missingUnarchive.Status.ShouldBe((HttpStatusCode)428, missingUnarchive.Body.ToString());
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct)).Body.GetProperty("state").GetString().ShouldBe("archived");

		(await AuditAsync(f, "agent.conversation.archived", id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.unarchived", id, ct)).ShouldBe(0);
	}

	[Fact]
	public async Task RepeatedRequestsSucceedWithoutChangeOrASecondAuditEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var alreadyActive = await UnarchiveAsync(f.Admin, id, "\"999\"", ct);
		alreadyActive.Status.ShouldBe(HttpStatusCode.OK, alreadyActive.Body.ToString());
		alreadyActive.Body.GetProperty("state").GetString().ShouldBe("active");
		alreadyActive.ETag.ShouldBe(etag);

		var first = await ArchiveAsync(f.Admin, id, etag, ct);
		first.Status.ShouldBe(HttpStatusCode.OK, first.Body.ToString());
		foreach (var ifMatch in new[] { etag, first.ETag, "\"999\"" })
		{
			var repeat = await ArchiveAsync(f.Admin, id, ifMatch, ct);
			repeat.Status.ShouldBe(HttpStatusCode.OK, repeat.Body.ToString());
			repeat.Body.GetProperty("state").GetString().ShouldBe("archived");
			repeat.ETag.ShouldBe(first.ETag);
		}

		var unarchived = await UnarchiveAsync(f.Admin, id, first.ETag, ct);
		unarchived.Status.ShouldBe(HttpStatusCode.OK, unarchived.Body.ToString());
		var repeatUnarchive = await UnarchiveAsync(f.Admin, id, first.ETag, ct);
		repeatUnarchive.Status.ShouldBe(HttpStatusCode.OK, repeatUnarchive.Body.ToString());
		repeatUnarchive.ETag.ShouldBe(unarchived.ETag);

		(await AuditAsync(f, "agent.conversation.archived", id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.unarchived", id, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ViewersAreForbiddenAndOtherMembersGetNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);
		using var viewer = await MemberAsync(f, "viewer-1", "viewer", ct);
		using var coach = await MemberAsync(f, "coach-1", "coach", ct);
		await TestMembers.SeedAsync(f.Db, "outsider-1", cancellationToken: ct);
		using var outsider = await ApiSession.SignInAsync(f.Factory, "outsider-1", TestMembers.DefaultPassword, ct);

		(await ArchiveAsync(viewer, id, etag, ct)).Status.ShouldBe(HttpStatusCode.Forbidden);
		(await UnarchiveAsync(viewer, id, etag, ct)).Status.ShouldBe(HttpStatusCode.Forbidden);
		var archive = await ArchiveAsync(coach, id, etag, ct);
		archive.Status.ShouldBe(HttpStatusCode.NotFound, archive.Body.ToString());
		(await UnarchiveAsync(coach, id, etag, ct)).Status.ShouldBe(HttpStatusCode.NotFound);
		(await ArchiveAsync(outsider, id, etag, ct)).Status.ShouldBe(HttpStatusCode.Forbidden);

		var archived = await ArchiveAsync(f.Admin, id, etag, ct);
		archived.Status.ShouldBe(HttpStatusCode.OK, archived.Body.ToString());
		(await UnarchiveAsync(viewer, id, archived.ETag, ct)).Status.ShouldBe(HttpStatusCode.Forbidden);
		(await UnarchiveAsync(coach, id, archived.ETag, ct)).Status.ShouldBe(HttpStatusCode.NotFound);
		(await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct)).Body.GetProperty("state").GetString().ShouldBe("archived");

		(await AuditAsync(f, "agent.conversation.archived", id, ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.unarchived", id, ct)).ShouldBe(0);
	}

	[Fact]
	public async Task ConcurrentArchiveAndUnarchiveYieldOneSuccessfulTransition()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var (id, etag) = await CreateAsync(f, f.Admin, run, ct);

		var archives = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ArchiveAsync(f.Admin, id, etag, ct)));
		archives.ShouldAllBe(r => r.Status == HttpStatusCode.OK);
		archives.Select(r => r.ETag).Distinct().Count().ShouldBe(1);
		(await AuditAsync(f, "agent.conversation.archived", id, ct)).ShouldBe(1);
		var archivedEtag = archives[0].ETag!;

		// Unarchive and archive race from the same archived version: whichever runs second finds either a satisfied state
		// or a stale version, never a second transition from that version.
		var race = await Task.WhenAll(
			UnarchiveAsync(f.Admin, id, archivedEtag, ct), ArchiveAsync(f.Admin, id, archivedEtag, ct));
		race.ShouldAllBe(r => r.Status == HttpStatusCode.OK || r.Status == HttpStatusCode.PreconditionFailed);
		race.Count(r => r.Status == HttpStatusCode.OK && r.ETag != archivedEtag).ShouldBeLessThanOrEqualTo(1);

		var final = await GetAsync(f.Admin, $"/api/v1/agent-conversations/{id}", ct);
		var unarchived = await AuditAsync(f, "agent.conversation.unarchived", id, ct);
		unarchived.ShouldBeLessThanOrEqualTo(1);
		final.Body.GetProperty("state").GetString().ShouldBe(unarchived == 1 ? "active" : "archived");
	}
}
