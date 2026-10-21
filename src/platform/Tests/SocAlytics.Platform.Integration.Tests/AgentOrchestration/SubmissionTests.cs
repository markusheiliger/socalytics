using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class SubmissionTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Video = "specialist-video-analysis";
	private const string Question = "Which pressing triggers did the team miss in the first half?";

	private static async Task<ApiSession> MemberAsync(AgentOrchestrationFixture f, string name, string role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{id}', '{f.Hierarchy.TeamId}', '{role}', now(), '{id}')",
			ct);
		return await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct);
	}

	private static Task<HttpResponseMessage> PostAsync(
		ApiSession session, string path, object body, string? key, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (key is not null)
		{
			request.Headers.Add("Idempotency-Key", key);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return session.Client.SendAsync(request, ct);
	}

	// Creates a conversation for the run and adds the given specialists.
	private static async Task<Guid> ConversationAsync(
		AgentOrchestrationFixture f, ApiSession owner, Guid runId, CancellationToken ct, params string[] agents)
	{
		using var created = await PostAsync(
			owner, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, Guid.NewGuid().ToString("N"), null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("conversationId").GetGuid();
		var etag = created.Headers.ETag!.ToString();
		foreach (var agent in agents)
		{
			using var added = await PostAsync(owner, $"/api/v1/agent-conversations/{id}/participants", new { agentId = agent }, null, etag, ct);
			added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
			etag = added.Headers.ETag!.ToString();
		}

		return id;
	}

	private static Task<HttpResponseMessage> SubmitAsync(
		ApiSession owner, Guid conversationId, string? key, string? agent, string? question, CancellationToken ct) =>
		PostAsync(owner, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = agent, question }, key, null, ct);

	private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(ct));
		if (code is not null)
		{
			(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe(code);
		}
	}

	private static async Task<(long Invocations, long Outbox)> CountsAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations", ct),
			await f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'agent-invocation-ready'", ct));

	[Fact]
	public async Task SubmissionIsAcceptedQueuedAndNotifiedWithoutContent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var conversation = await ConversationAsync(f, coach, run, ct, Tactics);

		using var accepted = await SubmitAsync(coach, conversation, "submit-1", Tactics, Question, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(ct));
		var body = await accepted.Content.ReadFromJsonAsync<JsonElement>(ct);
		var invocation = body.GetProperty("invocationId").GetGuid();
		body.GetProperty("conversationId").GetGuid().ShouldBe(conversation);
		accepted.Headers.Location!.ToString().ShouldBe($"/api/v1/agent-invocations/{invocation}");
		accepted.Headers.RetryAfter.ShouldNotBeNull();

		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocation}' AND state = 'queued' AND agent_id = '{Tactics}'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages", ct)).ShouldBe(1);

		await using var connection = new Npgsql.NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new Npgsql.NpgsqlCommand(
			"SELECT message_type, payload::text FROM socalytics.outbox_messages", connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		(await reader.ReadAsync(ct)).ShouldBeTrue();
		reader.GetString(0).ShouldBe("agent-invocation-ready");
		var text = reader.GetString(1);
		text.ShouldNotContain(Question);
		var payload = JsonDocument.Parse(text).RootElement.Clone();
		payload.GetProperty("invocationId").GetGuid().ShouldBe(invocation);
		payload.TryGetProperty("question", out _).ShouldBeFalse();
		var result = f.Factory.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.AgentWorkNotificationV1, payload);
		result.IsValid.ShouldBeTrue(string.Join(", ", result.Errors.Select(e => e.Field + ":" + e.Keyword)));
	}

	[Fact]
	public async Task RepeatsReplayEqualRequestsAndRefuseDifferentOnes()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var conversation = await ConversationAsync(f, coach, run, ct, Tactics, Video);

		using var first = await SubmitAsync(coach, conversation, "submit-1", Tactics, Question, ct);
		first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(ct));
		var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("invocationId").GetGuid();

		using var repeat = await SubmitAsync(coach, conversation, "submit-1", Tactics, Question, ct);
		repeat.StatusCode.ShouldBe(HttpStatusCode.Accepted, await repeat.Content.ReadAsStringAsync(ct));
		(await repeat.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("invocationId").GetGuid().ShouldBe(firstId);
		(await CountsAsync(f, ct)).ShouldBe((1, 1));

		using var otherQuestion = await SubmitAsync(coach, conversation, "submit-1", Tactics, Question + " Again.", ct);
		await AssertProblemAsync(otherQuestion, HttpStatusCode.Conflict, "idempotency-key-reused", ct);
		using var otherAgent = await SubmitAsync(coach, conversation, "submit-1", Video, Question, ct);
		await AssertProblemAsync(otherAgent, HttpStatusCode.Conflict, "idempotency-key-reused", ct);
		(await CountsAsync(f, ct)).ShouldBe((1, 1));
	}

	[Fact]
	public async Task RefusedSubmissionsChangeNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var conversation = await ConversationAsync(f, coach, run, ct, Tactics, Video);
		var maxQuestion = f.Factory.Services.GetRequiredService<IOptionsMonitor<AgentOrchestrationOptions>>().CurrentValue.Execution.MaxQuestionCharacters;

		using var nonParticipant = await SubmitAsync(coach, conversation, "k-1", "specialist-set-pieces", Question, ct);
		await AssertProblemAsync(nonParticipant, HttpStatusCode.Conflict, "agent-not-participant", ct);

		using var empty = await SubmitAsync(coach, conversation, "k-2", Tactics, "  ", ct);
		await AssertProblemAsync(empty, HttpStatusCode.BadRequest, null, ct);
		using var tooLong = await SubmitAsync(coach, conversation, "k-3", Tactics, new string('q', maxQuestion + 1), ct);
		await AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, null, ct);
		using var noKey = await SubmitAsync(coach, conversation, null, Tactics, Question, ct);
		await AssertProblemAsync(noKey, HttpStatusCode.BadRequest, "idempotency-key-missing", ct);

		await f.ScalarAsync($"UPDATE socalytics.agent_enablements SET agent_version = NULL WHERE agent_id = '{Video}'", ct);
		using var disabled = await SubmitAsync(coach, conversation, "k-4", Video, Question, ct);
		await AssertProblemAsync(disabled, HttpStatusCode.Conflict, "agent-not-available", ct);
		await f.ScalarAsync($"UPDATE socalytics.agent_enablements SET agent_version = '0.1.0' WHERE agent_id = '{Video}'", ct);

		await f.ScalarAsync($"UPDATE socalytics.team_role_assignment SET role = 'viewer' WHERE team_id = '{f.Hierarchy.TeamId}'", ct);
		using var viewer = await SubmitAsync(coach, conversation, "k-5", Tactics, Question, ct);
		await AssertProblemAsync(viewer, HttpStatusCode.Forbidden, null, ct);

		(await CountsAsync(f, ct)).ShouldBe((0, 0));
	}

	[Fact]
	public async Task TheSixthUnfinishedInvocationIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var conversation = await ConversationAsync(f, coach, run, ct, Tactics);
		var limit = f.Factory.Services.GetRequiredService<IOptionsMonitor<AgentOrchestrationOptions>>().CurrentValue.Execution.MaxUnfinishedInvocationsPerOwner;
		limit.ShouldBe(5);

		for (var i = 0; i < limit; i++)
		{
			using var accepted = await SubmitAsync(coach, conversation, $"k-{i}", Tactics, $"{Question} {i}", ct);
			accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(ct));
		}

		using var sixth = await SubmitAsync(coach, conversation, "k-sixth", Tactics, Question, ct);
		await AssertProblemAsync(sixth, HttpStatusCode.TooManyRequests, "too-many-unfinished-invocations", ct);
		(await CountsAsync(f, ct)).ShouldBe((limit, limit));
	}
}
