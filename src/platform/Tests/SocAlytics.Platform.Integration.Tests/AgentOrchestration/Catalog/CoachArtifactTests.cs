using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-008-FR-016, SC-003: Coach Agent artifacts enforce every architectural invariant, report profile distances, and need an
// acknowledged review at approval when a profile is too close to an approved one.
public sealed class CoachArtifactTests(PostgresContainerFixture postgres)
{
	private const string Klopp = "coach-jurgen-klopp";
	private const string Guardiola = "coach-pep-guardiola";
	private const string Mourinho = "coach-jose-mourinho";
	private const string Version = "1.0.0";

	private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? ETag)
	{
		public string[] Codes => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("violations", out var v)
			? [.. v.EnumerateArray().Select(x => x.GetProperty("code").GetString()!)]
			: [];
	}

	private sealed record Support(
		CatalogDocument Prompt, CatalogDocument Model, CatalogDocument Tools, CatalogDocument Safety, CatalogDocument Modifier);

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

	private static void ShouldBeRefused(Reply reply, params string[] codes)
	{
		ShouldBeProblem(reply, HttpStatusCode.BadRequest, "agent-catalog-validation-failed");
		reply.Codes.ShouldNotBeEmpty();
		foreach (var code in codes)
		{
			reply.Codes.ShouldContain(code, reply.Body.ToString());
		}
	}

	private static Task<long> StoredAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		f.ScalarAsync("SELECT (SELECT count(*) FROM socalytics.agent_catalog_artifacts) + (SELECT count(*) FROM socalytics.agent_versions)", ct);

	private static async Task<Reply> RegisterAsync(AgentCatalogApi api, CatalogDocument artifact, CancellationToken ct) =>
		await ReadAsync(await api.RegisterArtifactAsync(artifact, ct), ct);

	private static async Task<Reply> RegisterCreatedAsync(AgentCatalogApi api, CatalogDocument artifact, CancellationToken ct)
	{
		var reply = await RegisterAsync(api, artifact, ct);
		reply.Status.ShouldBe(HttpStatusCode.Created, $"{artifact.Kind} {artifact.Id}: {reply.Body}");
		return reply;
	}

	private static CatalogDocument DimensionSet() =>
		CatalogFixtures.Artifact(CatalogKinds.DimensionSets, "coach-philosophy-dimensions-1.0.0");

	private static CatalogDocument Profile(string agentId) =>
		CatalogFixtures.Artifact(CatalogKinds.PhilosophyProfiles, $"{agentId}-1.0.0");

	private static async Task<Support> RegisterSupportAsync(AgentCatalogApi api, CancellationToken ct)
	{
		var support = new Support(
			CatalogFixtures.Artifact(CatalogKinds.PromptTemplates, "coach-prompt"),
			CatalogFixtures.Artifact(CatalogKinds.ModelSelections, "simulated-minimal"),
			CatalogFixtures.Artifact(CatalogKinds.ToolPolicies, "full-allowlist"),
			CatalogFixtures.Artifact(CatalogKinds.SafetyPolicies, "coach-safety"),
			CatalogFixtures.Artifact(CatalogKinds.ModifierPolicies, "coach-modifier-policy-1.0.0"));
		await RegisterCreatedAsync(api, DimensionSet(), ct);
		await api.RegisterAllAsync([support.Prompt, support.Model, support.Tools, support.Safety, support.Modifier], ct);
		return support;
	}

	private static JsonObject CoachVersion(Support support, string agentId, CatalogDocument? profile)
	{
		var document = CatalogFixtures.LoadRaw("agent-version", "coach-jurgen-klopp-1.0.0");
		document["agentId"] = agentId;
		var pins = (JsonObject)document["artifacts"]!;
		CatalogFixtures.Pin(pins, "promptTemplate", support.Prompt);
		CatalogFixtures.Pin(pins, "modelSelection", support.Model);
		CatalogFixtures.Pin(pins, "toolPolicy", support.Tools);
		CatalogFixtures.Pin(pins, "safetyPolicy", support.Safety);
		CatalogFixtures.Pin(pins, "modifierPolicy", support.Modifier);
		if (profile is null)
		{
			pins.Remove("philosophyProfile");
		}
		else
		{
			CatalogFixtures.Pin(pins, "philosophyProfile", profile);
		}

		return document;
	}

	private static async Task<string> RegisterCoachVersionAsync(
		AgentCatalogApi api, Support support, string agentId, CatalogDocument profile, CancellationToken ct)
	{
		var registered = await ReadAsync(await api.RegisterAgentVersionAsync(Version, CoachVersion(support, agentId, profile), ct), ct);
		registered.Status.ShouldBe(HttpStatusCode.Created, registered.Body.ToString());
		return registered.ETag!;
	}

	private static async Task ApproveAsync(AgentCatalogApi api, string agentId, string etag, CancellationToken ct)
	{
		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(agentId, Version, etag, null, ct), ct);
		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
	}

	private static double DistanceTo(Reply reply, string agentId) =>
		reply.Body.GetProperty("distances").EnumerateArray()
			.Single(d => d.GetProperty("agentId").GetString() == agentId).GetProperty("distance").GetDouble();

	[Fact]
	public async Task TheDimensionSetAndThreeProfilesRegisterAndReportTheirDistances()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var support = await RegisterSupportAsync(api, ct);

		var klopp = Profile(Klopp);
		await RegisterCreatedAsync(api, klopp, ct);
		await ApproveAsync(api, Klopp, await RegisterCoachVersionAsync(api, support, Klopp, klopp, ct), ct);

		var guardiola = Profile(Guardiola);
		var guardiolaProfile = await RegisterCreatedAsync(api, guardiola, ct);
		DistanceTo(guardiolaProfile, Klopp).ShouldBe(0.25, 0.001);
		await ApproveAsync(api, Guardiola, await RegisterCoachVersionAsync(api, support, Guardiola, guardiola, ct), ct);

		var mourinho = Profile(Mourinho);
		var mourinhoProfile = await RegisterCreatedAsync(api, mourinho, ct);
		DistanceTo(mourinhoProfile, Klopp).ShouldBe(0.25, 0.001);
		DistanceTo(mourinhoProfile, Guardiola).ShouldBe(0.31, 0.001);
		mourinhoProfile.Body.GetProperty("distances").EnumerateArray().ShouldAllBe(d => !d.GetProperty("reviewRequired").GetBoolean());

		var read = await ReadAsync(await api.GetArtifactAsync(CatalogKinds.PhilosophyProfiles, Mourinho, Version, null, ct), ct);
		DistanceTo(read, Guardiola).ShouldBe(0.31, 0.001);

		await RegisterCoachVersionAsync(api, support, Mourinho, mourinho, ct);
		var version = await ReadAsync(await api.GetAgentVersionAsync(Mourinho, Version, null, ct), ct);
		DistanceTo(version, Klopp).ShouldBe(0.25, 0.001);
		DistanceTo(version, Guardiola).ShouldBe(0.31, 0.001);
	}

	[Fact]
	public async Task AMinorDimensionSetVersionWithChangedDimensionIdsIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		await RegisterCreatedAsync(api, DimensionSet(), ct);
		var stored = await StoredAsync(f, ct);

		var changed = DimensionSet().Edit(d =>
		{
			d["version"] = "1.1.0";
			((JsonArray)d["dimensions"]!).RemoveAt(7);
		});
		ShouldBeRefused(await RegisterAsync(api, changed, ct), "dimension-set-ids-changed");

		var major = DimensionSet().Edit(d =>
		{
			d["version"] = "2.0.0";
			((JsonArray)d["dimensions"]!).RemoveAt(7);
		});
		(await RegisterAsync(api, major, ct)).Status.ShouldBe(HttpStatusCode.Created);
		(await StoredAsync(f, ct)).ShouldBe(stored + 1);
	}

	[Fact]
	public async Task ProfilesBreakingTheWeightRulesAreRefusedNamingEveryViolation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		await RegisterCreatedAsync(api, DimensionSet(), ct);
		var stored = await StoredAsync(f, ct);
		var audit = await f.ScalarAsync(
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.catalog.record.registered' AND outcome = 'refused'", ct);

		var total99 = Profile(Klopp).Edit(p => p["weights"]!["game-state-adaptation"]!["weight"] = 4);
		ShouldBeRefused(await RegisterAsync(api, total99, ct), "weights-total-not-100");

		var several = Profile(Klopp).Edit(p =>
		{
			var weights = (JsonObject)p["weights"]!;
			var moved = weights["set-play-leverage"]!.DeepClone();
			weights.Remove("set-play-leverage");
			weights["unknown-dimension"] = moved;
			weights["pressing-intensity"]!["weight"] = 25;
		});
		ShouldBeRefused(await RegisterAsync(api, several, ct), "missing-dimension", "unknown-dimension", "weights-total-not-100");

		var added = Profile(Klopp).Edit(p => ((JsonObject)p["weights"]!)["extra-dimension"] = p["weights"]!["pressing-intensity"]!.DeepClone());
		ShouldBeRefused(await RegisterAsync(api, added, ct), "unknown-dimension", "weights-total-not-100");

		var missing = Profile(Klopp).Edit(p => ((JsonObject)p["weights"]!).Remove("game-state-adaptation"));
		ShouldBeRefused(await RegisterAsync(api, missing, ct), "missing-dimension", "weights-total-not-100");

		ShouldBeRefused(await RegisterAsync(api, Profile(Klopp).Edit(p => p["weights"]!["pressing-intensity"]!["weight"] = 20.5), ct));
		ShouldBeRefused(await RegisterAsync(api, Profile(Klopp).Edit(p => p["weights"]!["pressing-intensity"]!["weight"] = 120), ct));
		ShouldBeRefused(await RegisterAsync(api, Profile(Klopp).Edit(p => ((JsonObject)p["weights"]!["pressing-intensity"]!).Remove("rationale")), ct));
		ShouldBeRefused(await RegisterAsync(api, Profile(Klopp).Edit(p => ((JsonObject)p["weights"]!["pressing-intensity"]!).Remove("provenance")), ct));
		ShouldBeRefused(await RegisterAsync(api, Profile(Klopp).Edit(p => p["weights"]!["pressing-intensity"]!["rationale"] = " "), ct));

		var unknownSet = Profile(Klopp).Edit(p => p["dimensionSet"]!["version"] = "1.5.0");
		ShouldBeRefused(await RegisterAsync(api, unknownSet, ct), "dimension-set-not-found");

		var badSupersede = Profile(Klopp).Edit(p => p["supersedes"] = new JsonObject { ["version"] = "0.9.0" });
		ShouldBeRefused(await RegisterAsync(api, badSupersede, ct), "superseded-profile-invalid");
		var selfSupersede = Profile(Klopp).Edit(p => p["supersedes"] = new JsonObject { ["version"] = Version });
		ShouldBeRefused(await RegisterAsync(api, selfSupersede, ct), "superseded-profile-invalid");

		(await StoredAsync(f, ct)).ShouldBe(stored);
		(await f.ScalarAsync(
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.catalog.record.registered' AND outcome = 'refused'", ct)).ShouldBeGreaterThan(audit);

		await RegisterCreatedAsync(api, Profile(Klopp), ct);
		var supersedes = Profile(Klopp).Edit(p =>
		{
			p["version"] = "1.1.0";
			p["supersedes"] = new JsonObject { ["version"] = Version };
		});
		(await RegisterAsync(api, supersedes, ct)).Status.ShouldBe(HttpStatusCode.Created);
	}

	[Fact]
	public async Task ModifierPoliciesOverTheirBoundsOrWithAnUnpermittedCategoryAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var policy = CatalogFixtures.Artifact(CatalogKinds.ModifierPolicies, "coach-modifier-policy-1.0.0");

		ShouldBeRefused(await RegisterAsync(api, policy.Edit(p => p["perDimensionBound"] = 4), ct));
		ShouldBeRefused(await RegisterAsync(api, policy.Edit(p => p["maxPositiveMovement"] = 11), ct));
		ShouldBeRefused(await RegisterAsync(api, policy.Edit(p => ((JsonArray)p["contextCategories"]!).Add("opponent-name")), ct));
		ShouldBeRefused(await RegisterAsync(api, policy.Edit(p =>
		{
			p["perDimensionBound"] = 4;
			p["maxPositiveMovement"] = 11;
			((JsonArray)p["contextCategories"]!).Add("opponent-name");
		}), ct));
		(await StoredAsync(f, ct)).ShouldBe(0);

		(await RegisterAsync(api, policy, ct)).Status.ShouldBe(HttpStatusCode.Created);
	}

	[Fact]
	public async Task CoachPromptsAndSafetyPoliciesBreakingTheirRulesAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var prompt = CatalogFixtures.Artifact(CatalogKinds.PromptTemplates, "coach-prompt");
		var safety = CatalogFixtures.Artifact(CatalogKinds.SafetyPolicies, "coach-safety");

		var noSlot = prompt.Edit(p =>
		{
			p["text"] = "You are {{display_name}}.\nEvidence:\n{{evidence_context}}";
			p["slots"] = new JsonArray("display_name", "evidence_context");
		});
		ShouldBeRefused(await RegisterAsync(api, noSlot, ct), "schema-violation");

		var twoSlots = prompt.Edit(p => p["text"] = p["text"]!.GetValue<string>() + "\n{{philosophy_profile}}");
		ShouldBeRefused(await RegisterAsync(api, twoSlots, ct), "profile-slot-missing");

		var noDisclosureRules = safety.Edit(s => s["rules"] = new JsonArray("non-impersonation", "non-fabrication"));
		ShouldBeRefused(await RegisterAsync(api, noDisclosureRules, ct), "schema-violation");
		(await StoredAsync(f, ct)).ShouldBe(0);
	}

	[Fact]
	public async Task CoachAgentVersionsBreakingTheirRulesAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var support = await RegisterSupportAsync(api, ct);
		var klopp = Profile(Klopp);
		await RegisterCreatedAsync(api, klopp, ct);
		var stored = await StoredAsync(f, ct);

		var withoutProfile = CoachVersion(support, Klopp, null);
		ShouldBeRefused(await ReadAsync(await api.RegisterAgentVersionAsync(Version, withoutProfile, ct), ct), "artifact-not-found");

		var withoutDisclosure = CoachVersion(support, Klopp, klopp);
		withoutDisclosure.Remove("disclosure");
		ShouldBeRefused(await ReadAsync(await api.RegisterAgentVersionAsync(Version, withoutDisclosure, ct), ct));
		var blankDisclosure = CoachVersion(support, Klopp, klopp);
		blankDisclosure["disclosure"] = " ";
		ShouldBeRefused(await ReadAsync(await api.RegisterAgentVersionAsync(Version, blankDisclosure, ct), ct));

		var otherMajor = support.Prompt.Edit(p =>
		{
			p["version"] = "1.1.0";
			p["dimensionSetMajor"] = 2;
		});
		await RegisterCreatedAsync(api, otherMajor, ct);
		var mismatched = CoachVersion(support with { Prompt = otherMajor }, Klopp, klopp);
		ShouldBeRefused(await ReadAsync(await api.RegisterAgentVersionAsync(Version, mismatched, ct), ct), "dimension-set-incompatible");

		var otherModifier = support.Modifier.Edit(m =>
		{
			m["version"] = "2.0.0";
			m["dimensionSetMajor"] = 2;
		});
		await RegisterCreatedAsync(api, otherModifier, ct);
		var modifierMismatch = CoachVersion(support with { Modifier = otherModifier }, Klopp, klopp);
		ShouldBeRefused(await ReadAsync(await api.RegisterAgentVersionAsync(Version, modifierMismatch, ct), ct), "dimension-set-incompatible");

		(await StoredAsync(f, ct)).ShouldBe(stored + 2);
		(await ReadAsync(await api.RegisterAgentVersionAsync(Version, CoachVersion(support, Klopp, klopp), ct), ct)).Status
			.ShouldBe(HttpStatusCode.Created);
	}

	[Fact]
	public async Task ASyntheticProfileWithinTheThresholdNeedsTheAcknowledgementAtApproval()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var support = await RegisterSupportAsync(api, ct);
		var klopp = Profile(Klopp);
		await RegisterCreatedAsync(api, klopp, ct);
		await ApproveAsync(api, Klopp, await RegisterCoachVersionAsync(api, support, Klopp, klopp, ct), ct);

		// 4 points moved from Klopp's profile: a distance of 0.02.
		var synthetic = klopp.Edit(p =>
		{
			p["id"] = "coach-synthetic-fourth";
			p["weights"]!["pressing-intensity"]!["weight"] = 22;
			p["weights"]!["game-state-adaptation"]!["weight"] = 3;
		});
		var registered = await RegisterCreatedAsync(api, synthetic, ct);
		DistanceTo(registered, Klopp).ShouldBe(0.02, 0.001);
		registered.Body.GetProperty("distances").EnumerateArray().Single().GetProperty("reviewRequired").GetBoolean().ShouldBeTrue();

		var etag = await RegisterCoachVersionAsync(api, support, Guardiola, synthetic, ct);
		var refused = await ReadAsync(await api.ApproveAgentVersionAsync(Guardiola, Version, etag, null, ct), ct);
		ShouldBeProblem(refused, HttpStatusCode.Conflict, "differentiation-review-required");
		var notAcknowledged = await ReadAsync(await api.ApproveAgentVersionAsync(Guardiola, Version, etag, false, ct), ct);
		ShouldBeProblem(notAcknowledged, HttpStatusCode.Conflict, "differentiation-review-required");
		(await ReadAsync(await api.GetAgentVersionAsync(Guardiola, Version, null, ct), ct)).Body.GetProperty("state").GetString().ShouldBe("draft");

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(Guardiola, Version, etag, true, ct), ct);
		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
		approved.Body.GetProperty("state").GetString().ShouldBe("approved");
		approved.Body.GetProperty("differentiationAcknowledged").GetBoolean().ShouldBeTrue();
	}

	[Fact]
	public async Task AnApprovedCoachAgentVersionCannotBeEnabled()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var support = await RegisterSupportAsync(api, ct);
		var klopp = Profile(Klopp);
		await RegisterCreatedAsync(api, klopp, ct);
		await ApproveAsync(api, Klopp, await RegisterCoachVersionAsync(api, support, Klopp, klopp, ct), ct);

		var list = await ReadAsync(await f.Admin.Client.GetAsync("/api/v1/agent-catalog/enablements", ct), ct);
		var etag = list.Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("agentId").GetString() == Klopp)
			.GetProperty("etag").GetString()!;
		using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-catalog/enablements/{Klopp}")
		{
			Content = JsonContent.Create(new { version = Version }),
		};
		request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
		request.Headers.TryAddWithoutValidation("If-Match", etag);
		var refused = await ReadAsync(await f.Admin.Client.SendAsync(request, ct), ct);

		ShouldBeProblem(refused, HttpStatusCode.Conflict, "coach-agent-enablement-unsupported");
	}
}
