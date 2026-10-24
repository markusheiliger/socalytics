using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Enablement;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-016-FR-018, SC-001: enabling is a conditional, single-winner, audited replacement of the one enabled version per agent.
public sealed class EnablementTests(PostgresContainerFixture postgres)
{
	private const string AgentId = "specialist-tactics";
	private const string OtherAgentId = "specialist-video-analysis";
	private const string SeedVersion = "0.1.0";
	private const string Version = "1.0.0";
	private const string Root = "/api/v1/agent-catalog";

	private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? ETag);

	private sealed class AllowsNoSimulatedProvider : ICatalogEnvironment
	{
		public bool AllowsSimulatedProvider => false;
	}

	private static async Task<Reply> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using (response)
		{
			var text = await response.Content.ReadAsStringAsync(ct);
			var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
			var body = text.Length == 0 || !isJson ? default : JsonDocument.Parse(text).RootElement.Clone();
			return new Reply(response.StatusCode, body, response.Headers.ETag?.Tag);
		}
	}

	private static void ShouldBeProblem(Reply reply, HttpStatusCode status, string code)
	{
		reply.Status.ShouldBe(status, reply.Body.ToString());
		reply.Body.GetProperty("code").GetString().ShouldBe(code);
	}

	private static async Task<Reply> SendAsync(
		ApiSession session, HttpMethod method, string path, object? body, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await ReadAsync(await session.Client.SendAsync(request, ct), ct);
	}

	private static Task<Reply> ListAsync(ApiSession session, CancellationToken ct) =>
		SendAsync(session, HttpMethod.Get, $"{Root}/enablements", null, null, ct);

	private static Task<Reply> SetAsync(ApiSession session, string agentId, string? version, string? ifMatch, CancellationToken ct) =>
		SendAsync(session, HttpMethod.Put, $"{Root}/enablements/{agentId}", new { version }, ifMatch, ct);

	private static JsonElement ItemOf(Reply list, string agentId) =>
		list.Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("agentId").GetString() == agentId);

	private static async Task<string> EtagOfAsync(ApiSession session, string agentId, CancellationToken ct) =>
		ItemOf(await ListAsync(session, ct), agentId).GetProperty("etag").GetString()!;

	private static async Task<string?> EnabledVersionAsync(ApiSession session, string agentId, CancellationToken ct) =>
		ItemOf(await ListAsync(session, ct), agentId).GetProperty("enabledVersion").GetString();

	// Registers the specialist's artifacts and its version, optionally approving and retiring it, and returns the version's ETag.
	private static async Task<string> RegisterTacticsAsync(
		AgentCatalogApi api, CancellationToken ct, bool approve = false, bool retire = false, string model = "simulated-minimal")
	{
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts(model);
		var registered = await ReadAsync(await api.RegisterSpecialistAsync(artifacts, Version, ct), ct);
		registered.Status.ShouldBe(HttpStatusCode.Created, registered.Body.ToString());
		var etag = registered.ETag!;
		if (approve || retire)
		{
			var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);
			approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
			etag = approved.ETag!;
		}

		if (retire)
		{
			var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, etag, ct), ct);
			retired.Status.ShouldBe(HttpStatusCode.OK, retired.Body.ToString());
		}

		return etag;
	}

	private static Task<long> ChangeAuditAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		f.ScalarAsync(
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.catalog.enablement.changed' AND outcome = 'succeeded'", ct);

	private static async Task<ApiSession> MemberAsync(AgentOrchestrationFixture f, string name, string role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(f.Db, name, cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{id}', '{f.Hierarchy.TeamId}', '{role}', now(), '{id}')",
			ct);
		return await ApiSession.SignInAsync(f.Factory, name, TestMembers.DefaultPassword, ct);
	}

	[Fact]
	public async Task EnablingAnApprovedVersionReplacesThePreviousOneAndIsAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		await RegisterTacticsAsync(new AgentCatalogApi(f.Admin), ct, approve: true);
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(SeedVersion);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);
		var audited = await ChangeAuditAsync(f, ct);

		var enabled = await SetAsync(f.Admin, AgentId, Version, etag, ct);

		enabled.Status.ShouldBe(HttpStatusCode.OK, enabled.Body.ToString());
		enabled.Body.GetProperty("agentId").GetString().ShouldBe(AgentId);
		enabled.Body.GetProperty("group").GetString().ShouldBe("specialist");
		enabled.Body.GetProperty("enabledVersion").GetString().ShouldBe(Version);
		enabled.Body.GetProperty("changedBy").GetString().ShouldNotBeNullOrEmpty();
		enabled.ETag.ShouldNotBeNull().ShouldNotBe(etag);
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(Version);
		(await EnabledVersionAsync(f.Admin, OtherAgentId, ct)).ShouldBe(SeedVersion);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_enablements WHERE agent_id = '{AgentId}' AND agent_version = '{Version}'", ct))
			.ShouldBe(1);
		(await ChangeAuditAsync(f, ct)).ShouldBe(audited + 1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.catalog.enablement.changed' AND details::text LIKE '%{SeedVersion}%' AND details::text LIKE '%{Version}%'",
			ct)).ShouldBeGreaterThanOrEqualTo(1);

		var listed = await SendAsync(f.Admin, HttpMethod.Get, $"{Root}/agents", null, null, ct);
		listed.Body.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("agentId").GetString() == AgentId)
			.GetProperty("enabledVersion").GetString().ShouldBe(Version);

		var again = await SetAsync(f.Admin, AgentId, SeedVersion, enabled.ETag, ct);
		again.Status.ShouldBe(HttpStatusCode.OK, again.Body.ToString());
		again.Body.GetProperty("enabledVersion").GetString().ShouldBe(SeedVersion);
	}

	[Fact]
	public async Task DraftRetiredAndUnknownVersionsAreRefusedWithTheirReasons()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var api = new AgentCatalogApi(f.Admin);
		await RegisterTacticsAsync(api, ct);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);
		var audited = await ChangeAuditAsync(f, ct);

		var draft = await SetAsync(f.Admin, AgentId, Version, etag, ct);
		ShouldBeProblem(draft, HttpStatusCode.Conflict, "agent-version-not-enableable");
		draft.Body.GetProperty("reason").GetString().ShouldBe("not-approved");

		var unknown = await SetAsync(f.Admin, AgentId, "9.9.9", etag, ct);
		unknown.Status.ShouldBe(HttpStatusCode.BadRequest, unknown.Body.ToString());

		var invalid = await SetAsync(f.Admin, AgentId, "not-a-version", etag, ct);
		invalid.Status.ShouldBe(HttpStatusCode.BadRequest, invalid.Body.ToString());

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, null, null, ct), ct);
		approved.Status.ShouldBe(HttpStatusCode.PreconditionRequired);
		var current = await ReadAsync(await api.GetAgentVersionAsync(AgentId, Version, null, ct), ct);
		approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, current.ETag, null, ct), ct);
		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
		var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, approved.ETag, ct), ct);
		retired.Status.ShouldBe(HttpStatusCode.OK, retired.Body.ToString());

		var afterRetirement = await SetAsync(f.Admin, AgentId, Version, etag, ct);
		ShouldBeProblem(afterRetirement, HttpStatusCode.Conflict, "agent-version-not-enableable");
		afterRetirement.Body.GetProperty("reason").GetString().ShouldBe("retired");

		var missing = await SetAsync(f.Admin, "specialist-unknown", Version, etag, ct);
		missing.Status.ShouldBe(HttpStatusCode.NotFound, missing.Body.ToString());

		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(SeedVersion);
		(await ChangeAuditAsync(f, ct)).ShouldBe(audited);
		(await EtagOfAsync(f.Admin, AgentId, ct)).ShouldBe(etag);
	}

	[Fact]
	public async Task CoachAgentVersionsCannotBeEnabled()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var api = new AgentCatalogApi(f.Admin);
		CatalogDocument[] coachArtifacts =
		[
			CatalogFixtures.Artifact(CatalogKinds.PromptTemplates, "coach-prompt"),
			CatalogFixtures.Artifact(CatalogKinds.ModelSelections, "simulated-minimal"),
			CatalogFixtures.Artifact(CatalogKinds.ToolPolicies, "full-allowlist"),
			CatalogFixtures.Artifact(CatalogKinds.SafetyPolicies, "coach-safety"),
			CatalogFixtures.Artifact(CatalogKinds.DimensionSets, "coach-philosophy-dimensions-1.0.0"),
			CatalogFixtures.Artifact(CatalogKinds.PhilosophyProfiles, "coach-jurgen-klopp-1.0.0"),
			CatalogFixtures.Artifact(CatalogKinds.ModifierPolicies, "coach-modifier-policy-1.0.0"),
		];
		await api.RegisterAllAsync(coachArtifacts, ct);
		var document = CatalogFixtures.LoadRaw("agent-version", "coach-jurgen-klopp-1.0.0");
		var pins = (JsonObject)document["artifacts"]!;
		string[] slots = ["promptTemplate", "modelSelection", "toolPolicy", "safetyPolicy", "philosophyProfile", "modifierPolicy"];
		CatalogDocument[] pinned =
			[coachArtifacts[0], coachArtifacts[1], coachArtifacts[2], coachArtifacts[3], coachArtifacts[5], coachArtifacts[6]];
		for (var i = 0; i < slots.Length; i++)
		{
			CatalogFixtures.Pin(pins, slots[i], pinned[i]);
		}

		var registered = await ReadAsync(await api.RegisterAgentVersionAsync(Version, document, ct), ct);
		registered.Status.ShouldBe(HttpStatusCode.Created, registered.Body.ToString());
		var etag = await EtagOfAsync(f.Admin, "coach-jurgen-klopp", ct);

		var refused = await SetAsync(f.Admin, "coach-jurgen-klopp", Version, etag, ct);

		ShouldBeProblem(refused, HttpStatusCode.Conflict, "coach-agent-enablement-unsupported");
		(await EnabledVersionAsync(f.Admin, "coach-jurgen-klopp", ct)).ShouldBeNull();
	}

	[Fact]
	public async Task SimulatedProviderVersionsAreRefusedOutsideDevelopmentAndTest()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct, configureServices: services => services.Replace(ServiceDescriptor.Singleton<ICatalogEnvironment>(new AllowsNoSimulatedProvider())));
		var api = new AgentCatalogApi(f.Admin);
		await RegisterTacticsAsync(api, ct, approve: true);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);

		var refused = await SetAsync(f.Admin, AgentId, Version, etag, ct);

		ShouldBeProblem(refused, HttpStatusCode.Conflict, "agent-version-not-enableable");
		refused.Body.GetProperty("reason").GetString().ShouldBe("simulated-provider");
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(SeedVersion);
	}

	[Fact]
	public async Task NonSimulatedProviderVersionsAreEnableableOutsideDevelopmentAndTest()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct, configureServices: services => services.Replace(ServiceDescriptor.Singleton<ICatalogEnvironment>(new AllowsNoSimulatedProvider())));
		await RegisterTacticsAsync(new AgentCatalogApi(f.Admin), ct, approve: true, model: "specialist-openai");
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);

		var enabled = await SetAsync(f.Admin, AgentId, Version, etag, ct);

		enabled.Status.ShouldBe(HttpStatusCode.OK, enabled.Body.ToString());
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(Version);
	}

	[Fact]
	public async Task DisablingRemovesTheAgentAndRefusesParticipantsAndSubmissions()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var created = await SendWithKeyAsync(
			coach, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = run }, "disable-key", null, ct);
		created.Status.ShouldBe(HttpStatusCode.Created, created.Body.ToString());
		var conversation = created.Body.GetProperty("conversationId").GetGuid();
		var added = await SendAsync(
			coach, HttpMethod.Post, $"/api/v1/agent-conversations/{conversation}/participants", new { agentId = AgentId }, created.ETag, ct);
		added.Status.ShouldBe(HttpStatusCode.OK, added.Body.ToString());
		var audited = await ChangeAuditAsync(f, ct);

		var disabled = await SetAsync(f.Admin, AgentId, null, await EtagOfAsync(f.Admin, AgentId, ct), ct);

		disabled.Status.ShouldBe(HttpStatusCode.OK, disabled.Body.ToString());
		disabled.Body.GetProperty("enabledVersion").ValueKind.ShouldBe(JsonValueKind.Null);
		(await ChangeAuditAsync(f, ct)).ShouldBe(audited + 1);
		var available = await SendAsync(f.Admin, HttpMethod.Get, $"{Root}/agents", null, null, ct);
		available.Body.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("agentId").GetString())
			.ShouldBe([OtherAgentId]);

		var second = await SendAsync(
			coach, HttpMethod.Post, $"/api/v1/agent-conversations/{conversation}/participants", new { agentId = AgentId }, added.ETag, ct);
		ShouldBeProblem(second, HttpStatusCode.Conflict, "agent-not-available");
		var submitted = await SendWithKeyAsync(
			coach, HttpMethod.Post, $"/api/v1/agent-conversations/{conversation}/invocations",
			new { agentId = AgentId, question = "How do we press?" }, "disabled-submit", null, ct);
		ShouldBeProblem(submitted, HttpStatusCode.Conflict, "agent-not-available");
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_invocations", ct)).ShouldBe(0);

		var disabledAgain = await SetAsync(f.Admin, AgentId, null, disabled.ETag, ct);
		disabledAgain.Status.ShouldBe(HttpStatusCode.OK, disabledAgain.Body.ToString());
	}

	[Fact]
	public async Task StaleAndMissingIfMatchAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		await RegisterTacticsAsync(new AgentCatalogApi(f.Admin), ct, approve: true);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);
		var audited = await ChangeAuditAsync(f, ct);

		(await SetAsync(f.Admin, AgentId, Version, null, ct)).Status.ShouldBe(HttpStatusCode.PreconditionRequired);
		(await SetAsync(f.Admin, AgentId, Version, "\"999\"", ct)).Status.ShouldBe(HttpStatusCode.PreconditionFailed);
		(await SetAsync(f.Admin, AgentId, null, "\"999\"", ct)).Status.ShouldBe(HttpStatusCode.PreconditionFailed);

		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(SeedVersion);
		(await ChangeAuditAsync(f, ct)).ShouldBe(audited);

		(await SetAsync(f.Admin, AgentId, Version, etag, ct)).Status.ShouldBe(HttpStatusCode.OK);
		(await SetAsync(f.Admin, AgentId, SeedVersion, etag, ct)).Status.ShouldBe(HttpStatusCode.PreconditionFailed);
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(Version);
	}

	[Fact]
	public async Task ConcurrentChangesLeaveOneWinner()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		await TestMembers.SeedAsync(f.Db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		using var other = await ApiSession.SignInAsync(f.Factory, "admin-2", TestMembers.DefaultPassword, ct);
		await RegisterTacticsAsync(new AgentCatalogApi(f.Admin), ct, approve: true);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);
		var audited = await ChangeAuditAsync(f, ct);

		var replies = await Task.WhenAll(
			Task.Run(() => SetAsync(f.Admin, AgentId, Version, etag, ct), ct),
			Task.Run(() => SetAsync(other, AgentId, null, etag, ct), ct));

		replies.Count(r => r.Status == HttpStatusCode.OK).ShouldBe(1);
		replies.Single(r => r.Status != HttpStatusCode.OK).Status.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
		(await ChangeAuditAsync(f, ct)).ShouldBe(audited + 1);
		var winner = replies.Single(r => r.Status == HttpStatusCode.OK);
		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(winner.Body.GetProperty("enabledVersion").GetString());
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_enablements WHERE agent_id = '{AgentId}'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task OnlyClubAdminsMayReadOrChangeEnablements()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var coach = await MemberAsync(f, "coach-1", "coach", ct);
		var etag = await EtagOfAsync(f.Admin, AgentId, ct);

		ShouldBeProblem(await ListAsync(coach, ct), HttpStatusCode.Forbidden, "forbidden");
		var changed = await SetAsync(coach, AgentId, null, etag, ct);
		changed.Status.ShouldBe(HttpStatusCode.Forbidden, changed.Body.ToString());

		(await EnabledVersionAsync(f.Admin, AgentId, ct)).ShouldBe(SeedVersion);
		(await EtagOfAsync(f.Admin, AgentId, ct)).ShouldBe(etag);
	}

	private static async Task<Reply> SendWithKeyAsync(
		ApiSession session, HttpMethod method, string path, object body, string key, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await ReadAsync(await session.Client.SendAsync(request, ct), ct);
	}
}
