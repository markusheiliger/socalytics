using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class ConversationTests(PostgresContainerFixture postgres)
{
	private static async Task<(HttpStatusCode Status, JsonElement Body, string? ETag)> SendAsync(
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
		return (response.StatusCode, json, response.Headers.ETag?.Tag);
	}

	private static string CreatePath(Guid matchId) => $"/api/v1/matches/{matchId}/agent-conversations";

	private static Task<(HttpStatusCode Status, JsonElement Body, string? ETag)> CreateAsync(
		ApiSession session, Guid matchId, Guid runId, string key, CancellationToken ct) =>
		SendAsync(session, HttpMethod.Post, CreatePath(matchId), new { analysisRunId = runId }, key, null, ct);

	private static Task<(HttpStatusCode Status, JsonElement Body, string? ETag)> AddAsync(
		ApiSession session, Guid conversationId, string agentId, string? ifMatch, CancellationToken ct) =>
		SendAsync(session, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId }, null, ifMatch, ct);

	private static void ShouldBeProblem(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code)
	{
		status.ShouldBe(expected, body.ToString());
		body.GetProperty("code").GetString().ShouldBe(code);
	}

	private static async Task<ApiSession> MemberAsync(
		AgentOrchestrationFixture f, string name, string? teamRole, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		if (teamRole is not null)
		{
			var (status, body, _) = await SendAsync(
				f.Admin, HttpMethod.Put, $"/api/v1/members/{id}/team-roles/{f.Hierarchy.TeamId}", new { role = teamRole }, null, null, ct);
			status.ShouldBe(HttpStatusCode.OK, body.ToString());
		}

		return await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct);
	}

	// Binds the run to a recording set version finalized at the given time.
	private static async Task BindRunToNewSetAsync(AgentOrchestrationFixture f, Guid runId, DateTimeOffset finalizedAt, CancellationToken ct)
	{
		var set = Guid.NewGuid();
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"""
			INSERT INTO socalytics.recording_set_versions (id, match_id, team_id, member_count, created_by, finalized_at)
			VALUES (@set, @match, @team, 1, (SELECT id FROM socalytics.member_account WHERE account_name = 'admin-1'), @at);
			UPDATE socalytics.analysis_runs SET recording_set_version_id = @set WHERE id = @run;
			""",
			connection);
		command.Parameters.AddWithValue("set", set);
		command.Parameters.AddWithValue("match", f.Hierarchy.MatchId);
		command.Parameters.AddWithValue("team", f.Hierarchy.TeamId);
		command.Parameters.AddWithValue("at", finalizedAt);
		command.Parameters.AddWithValue("run", runId);
		await command.ExecuteNonQueryAsync(ct);
	}

	[Fact]
	public async Task ClubAdminsAndCoachesCreateConversationsForTerminalSuccessfulRuns()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		using var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var completed = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var partial = await f.SeedRunAsync(SeededRunState.PartiallyCompleted, ct: ct);

		var admin = await CreateAsync(f.Admin, f.Hierarchy.MatchId, completed, "admin-key", ct);
		admin.Status.ShouldBe(HttpStatusCode.Created, admin.Body.ToString());
		admin.Body.GetProperty("analysisRunId").GetGuid().ShouldBe(completed);
		admin.Body.GetProperty("matchId").GetGuid().ShouldBe(f.Hierarchy.MatchId);
		admin.Body.GetProperty("participants").GetArrayLength().ShouldBe(0);
		admin.ETag.ShouldNotBeNull();

		var coachConversation = await CreateAsync(coach, f.Hierarchy.MatchId, partial, "coach-key", ct);
		coachConversation.Status.ShouldBe(HttpStatusCode.Created, coachConversation.Body.ToString());
		coachConversation.Body.GetProperty("analysisRunId").GetGuid().ShouldBe(partial);
	}

	[Fact]
	public async Task ViewersAreForbiddenAndMembersWithoutTeamRoleGetNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		using var viewer = await MemberAsync(f, "viewer-1", "viewer", ct);
		using var outsider = await MemberAsync(f, "outsider-1", null, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);

		var forbidden = await CreateAsync(viewer, f.Hierarchy.MatchId, run, "viewer-key", ct);
		forbidden.Status.ShouldBe(HttpStatusCode.Forbidden, forbidden.Body.ToString());

		var notFound = await CreateAsync(outsider, f.Hierarchy.MatchId, run, "outsider-key", ct);
		notFound.Status.ShouldBe(HttpStatusCode.NotFound, notFound.Body.ToString());

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_conversations", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task IneligibleRunsAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var index = 0;
		foreach (var state in new[] { SeededRunState.Running, SeededRunState.Failed, SeededRunState.Cancelled })
		{
			var run = await f.SeedRunAsync(state, ct: ct);
			var result = await CreateAsync(f.Admin, f.Hierarchy.MatchId, run, $"ineligible-{index++}", ct);
			ShouldBeProblem(result.Status, result.Body, HttpStatusCode.Conflict, "analysis-run-not-eligible");
		}

		var (otherMatch, otherRun) = await f.CreateMatchWithRunAsync(SeededRunState.Completed, ct);
		otherMatch.ShouldNotBe(f.Hierarchy.MatchId);
		var foreign = await CreateAsync(f.Admin, f.Hierarchy.MatchId, otherRun, "foreign-run", ct);
		ShouldBeProblem(foreign.Status, foreign.Body, HttpStatusCode.Conflict, "analysis-run-not-eligible");

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_conversations", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task KeyReplayReturnsTheSameConversationAndKeyReuseWithAnotherRunConflicts()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var other = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);

		var first = await CreateAsync(f.Admin, f.Hierarchy.MatchId, run, "same-key", ct);
		first.Status.ShouldBe(HttpStatusCode.Created, first.Body.ToString());
		var replay = await CreateAsync(f.Admin, f.Hierarchy.MatchId, run, "same-key", ct);
		replay.Status.ShouldBe(HttpStatusCode.OK, replay.Body.ToString());
		replay.Body.GetProperty("conversationId").GetGuid().ShouldBe(first.Body.GetProperty("conversationId").GetGuid());

		var reused = await CreateAsync(f.Admin, f.Hierarchy.MatchId, other, "same-key", ct);
		ShouldBeProblem(reused.Status, reused.Body, HttpStatusCode.Conflict, "idempotency-key-reused");

		var missing = await SendAsync(f.Admin, HttpMethod.Post, CreatePath(f.Hierarchy.MatchId), new { analysisRunId = run }, null, null, ct);
		ShouldBeProblem(missing.Status, missing.Body, HttpStatusCode.BadRequest, "idempotency-key-missing");

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_conversations", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ParticipantsAreLimitedToEnabledSpecialists()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var created = await CreateAsync(f.Admin, f.Hierarchy.MatchId, run, "participants-key", ct);
		var conversation = created.Body.GetProperty("conversationId").GetGuid();
		var etag = created.ETag!;

		foreach (var unavailable in new[] { "coach-jurgen-klopp", "specialist-unknown", "specialist-fitness" })
		{
			var refused = await AddAsync(f.Admin, conversation, unavailable, etag, ct);
			ShouldBeProblem(refused.Status, refused.Body, HttpStatusCode.Conflict, "agent-not-available");
		}

		var stale = await AddAsync(f.Admin, conversation, "specialist-tactics", "\"999\"", ct);
		stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.Body.ToString());

		var added = await AddAsync(f.Admin, conversation, "specialist-tactics", etag, ct);
		added.Status.ShouldBe(HttpStatusCode.OK, added.Body.ToString());
		added.Body.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("agentId").GetString())
			.ShouldBe(["specialist-tactics"]);
		added.ETag.ShouldNotBe(etag);

		var duplicate = await AddAsync(f.Admin, conversation, "specialist-tactics", added.ETag, ct);
		ShouldBeProblem(duplicate.Status, duplicate.Body, HttpStatusCode.Conflict, "participant-exists");

		var staleAfterChange = await AddAsync(f.Admin, conversation, "specialist-video-analysis", etag, ct);
		staleAfterChange.Status.ShouldBe(HttpStatusCode.PreconditionFailed, staleAfterChange.Body.ToString());

		var second = await AddAsync(f.Admin, conversation, "specialist-video-analysis", added.ETag, ct);
		second.Status.ShouldBe(HttpStatusCode.OK, second.Body.ToString());
		second.Body.GetProperty("participants").GetArrayLength().ShouldBe(2);
	}

	[Fact]
	public async Task AgentListShowsExactlyTheEnabledSpecialists()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (status, body, _) = await SendAsync(f.Admin, HttpMethod.Get, "/api/v1/agent-catalog/agents", null, null, null, ct);
		status.ShouldBe(HttpStatusCode.OK, body.ToString());
		var agents = body.GetProperty("agents").EnumerateArray().ToList();
		agents.Select(a => a.GetProperty("agentId").GetString()).ShouldBe(
			["specialist-tactics", "specialist-video-analysis"], ignoreOrder: true);
		agents.ShouldAllBe(a => a.GetProperty("group").GetString() == "specialist");
	}

	[Fact]
	public async Task SupersededRunKeepsTheConversationUsable()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		await BindRunToNewSetAsync(f, run, f.Now.AddHours(-1), ct);

		var created = await CreateAsync(f.Admin, f.Hierarchy.MatchId, run, "superseded-key", ct);
		created.Status.ShouldBe(HttpStatusCode.Created, created.Body.ToString());
		created.Body.GetProperty("analysisRunIsCurrent").GetBoolean().ShouldBeTrue();
		var conversation = created.Body.GetProperty("conversationId").GetGuid();

		var newer = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		await BindRunToNewSetAsync(f, newer, f.Now, ct);

		var read = await SendAsync(f.Admin, HttpMethod.Get, $"/api/v1/agent-conversations/{conversation}", null, null, null, ct);
		read.Status.ShouldBe(HttpStatusCode.OK, read.Body.ToString());
		read.Body.GetProperty("analysisRunIsCurrent").GetBoolean().ShouldBeFalse();
		read.Body.GetProperty("analysisRunId").GetGuid().ShouldBe(run);

		var added = await AddAsync(f.Admin, conversation, "specialist-tactics", created.ETag, ct);
		added.Status.ShouldBe(HttpStatusCode.OK, added.Body.ToString());
		added.Body.GetProperty("analysisRunIsCurrent").GetBoolean().ShouldBeFalse();
	}
}
