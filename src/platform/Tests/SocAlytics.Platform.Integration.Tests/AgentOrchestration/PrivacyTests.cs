using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class PrivacyTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "Which pressing triggers did the team miss in the first half?";

	private sealed record Owned(Guid ConversationId, Guid InvocationId);

	private static async Task<(Guid Id, ApiSession Session)> MemberAsync(
		AgentOrchestrationFixture f, string name, string? teamRole, Guid teamId, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		if (teamRole is not null)
		{
			using var assigned = await f.Admin.SendAsync(HttpMethod.Put, $"/api/v1/members/{id}/team-roles/{teamId}", new { role = teamRole }, ct);
			assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(ct));
		}

		return (id, await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct));
	}

	private static async Task<HttpResponseMessage> PostAsync(
		ApiSession session, string path, object body, string? key, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (key is not null)
		{
			request.Headers.Add("Idempotency-Key", key);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await session.Client.SendAsync(request, ct);
	}

	// A conversation of the owner with one queued invocation.
	private static async Task<Owned> SeedOwnedAsync(AgentOrchestrationFixture f, ApiSession owner, CancellationToken ct)
	{
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await PostAsync(
			owner, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = run }, "create-" + Guid.NewGuid().ToString("N"), null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("conversationId").GetGuid();
		using var added = await PostAsync(
			owner, $"/api/v1/agent-conversations/{conversation}/participants", new { agentId = Tactics }, null, created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		using var submitted = await PostAsync(
			owner, $"/api/v1/agent-conversations/{conversation}/invocations", new { agentId = Tactics, question = Question }, "submit-" + Guid.NewGuid().ToString("N"), null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocation = (await submitted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("invocationId").GetGuid();
		return new Owned(conversation, invocation);
	}

	private static async Task<(HttpStatusCode Status, string? Code, string Body)> GetAsync(ApiSession session, string path, CancellationToken ct)
	{
		using var response = await session.SendAsync(HttpMethod.Get, path, null, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		string? code = null;
		if (response.StatusCode == HttpStatusCode.NotFound && text.Length > 0)
		{
			code = JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
		}

		return (response.StatusCode, code, text);
	}

	private static IEnumerable<string> ResourcePaths(Owned owned) =>
	[
		$"/api/v1/agent-conversations/{owned.ConversationId}",
		$"/api/v1/agent-conversations/{owned.ConversationId}/turns",
		$"/api/v1/agent-invocations/{owned.InvocationId}",
		$"/api/v1/agent-invocations/{owned.InvocationId}/lineage",
	];

	private static async Task AssertHiddenAsync(
		AgentOrchestrationFixture f, ApiSession caller, string who, Owned owned, CancellationToken ct)
	{
		var unknown = await GetAsync(caller, $"/api/v1/agent-conversations/{Guid.NewGuid()}", ct);
		unknown.Status.ShouldBe(HttpStatusCode.NotFound, who);
		foreach (var path in ResourcePaths(owned))
		{
			var result = await GetAsync(caller, path, ct);
			result.Status.ShouldBe(HttpStatusCode.NotFound, $"{who}: {path}");
			result.Code.ShouldBe(unknown.Code, $"{who}: {path}");
			result.Body.ShouldNotContain(Question, Case.Insensitive, $"{who}: {path}");
		}

		// The list is scoped to the owner, so the conversation never appears in it.
		var list = await GetAsync(caller, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", ct);
		if (list.Status == HttpStatusCode.OK)
		{
			list.Body.ShouldNotContain(owned.ConversationId.ToString(), Case.Insensitive, who);
		}
		else
		{
			list.Status.ShouldBe(HttpStatusCode.NotFound, who);
		}

		using var submit = await PostAsync(
			caller, $"/api/v1/agent-conversations/{owned.ConversationId}/invocations", new { agentId = Tactics, question = "intrusion" },
			"intrusion-" + Guid.NewGuid().ToString("N"), null, ct);
		((int)submit.StatusCode).ShouldBeOneOf([403, 404], who);
	}

	[Fact]
	public async Task OtherMembersGetTheSameNotFoundForEveryResourceOfTheConversation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (_, owner) = await MemberAsync(f, "owner-1", "coach", f.Hierarchy.TeamId, ct);
		using var _o = owner;
		var owned = await SeedOwnedAsync(f, owner, ct);

		// The owner reads everything.
		foreach (var path in ResourcePaths(owned))
		{
			(await GetAsync(owner, path, ct)).Status.ShouldBe(HttpStatusCode.OK, path);
		}

		var (_, otherCoach) = await MemberAsync(f, "coach-2", "coach", f.Hierarchy.TeamId, ct);
		var (_, viewer) = await MemberAsync(f, "viewer-1", "viewer", f.Hierarchy.TeamId, ct);
		var (otherTeam, _) = await new ClubHierarchyBuilder(f.Factory, f.Admin).CreateTeamWithMatchAsync(f.Hierarchy.SeasonId, ct);
		var (_, otherTeamCoach) = await MemberAsync(f, "coach-other-team", "coach", otherTeam, ct);
		var (_, otherTeamViewer) = await MemberAsync(f, "viewer-other-team", "viewer", otherTeam, ct);
		var (_, outsider) = await MemberAsync(f, "outsider-1", null, f.Hierarchy.TeamId, ct);
		using var _1 = otherCoach;
		using var _2 = viewer;
		using var _3 = otherTeamCoach;
		using var _4 = otherTeamViewer;
		using var _5 = outsider;

		// The Club Admin of the team is another member than the owner.
		await AssertHiddenAsync(f, f.Admin, "club admin", owned, ct);
		await AssertHiddenAsync(f, otherCoach, "other coach", owned, ct);
		await AssertHiddenAsync(f, viewer, "viewer", owned, ct);
		await AssertHiddenAsync(f, otherTeamCoach, "coach of another team", owned, ct);
		await AssertHiddenAsync(f, otherTeamViewer, "viewer of another team", owned, ct);
		await AssertHiddenAsync(f, outsider, "member without team role", owned, ct);
	}

	[Fact]
	public async Task OwnerWithOnlyAViewerRoleStillReadsButCannotSubmit()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (ownerId, owner) = await MemberAsync(f, "owner-1", "coach", f.Hierarchy.TeamId, ct);
		using var _o = owner;
		var owned = await SeedOwnedAsync(f, owner, ct);

		using (var downgraded = await f.Admin.SendAsync(
			HttpMethod.Put, $"/api/v1/members/{ownerId}/team-roles/{f.Hierarchy.TeamId}", new { role = "viewer" }, ct))
		{
			downgraded.StatusCode.ShouldBe(HttpStatusCode.OK, await downgraded.Content.ReadAsStringAsync(ct));
		}

		foreach (var path in ResourcePaths(owned))
		{
			(await GetAsync(owner, path, ct)).Status.ShouldBe(HttpStatusCode.OK, path);
		}

		var list = await GetAsync(owner, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", ct);
		list.Status.ShouldBe(HttpStatusCode.OK);
		list.Body.ShouldContain(owned.ConversationId.ToString(), Case.Insensitive);

		using var submit = await PostAsync(
			owner, $"/api/v1/agent-conversations/{owned.ConversationId}/invocations", new { agentId = Tactics, question = "another" },
			"viewer-submit", null, ct);
		submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await submit.Content.ReadAsStringAsync(ct));
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task OwnerWithoutATeamRoleGetsNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (ownerId, owner) = await MemberAsync(f, "owner-1", "coach", f.Hierarchy.TeamId, ct);
		using var _o = owner;
		var owned = await SeedOwnedAsync(f, owner, ct);

		using (var revoked = await f.Admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{ownerId}/team-roles/{f.Hierarchy.TeamId}", null, ct))
		{
			revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync(ct));
		}

		await AssertHiddenAsync(f, owner, "owner without team role", owned, ct);
	}

	[Fact]
	public async Task DeactivatedOwnerGetsNoAccess()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (ownerId, owner) = await MemberAsync(f, "owner-1", "coach", f.Hierarchy.TeamId, ct);
		using var _o = owner;
		var owned = await SeedOwnedAsync(f, owner, ct);

		using (var deactivated = await f.Admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{ownerId}/deactivate", null, ct))
		{
			deactivated.StatusCode.ShouldBe(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync(ct));
		}

		// Deactivation ends the sessions, so the old session is unauthenticated and reveals nothing.
		foreach (var path in ResourcePaths(owned))
		{
			var result = await GetAsync(owner, path, ct);
			((int)result.Status).ShouldBeOneOf([401, 404], path);
			result.Body.ShouldNotContain(Question, Case.Insensitive, path);
		}
	}
}
