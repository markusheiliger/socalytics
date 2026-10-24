using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-003-FR-009, FR-014, FR-024, FR-025, FR-025a, SC-002, SC-003, SC-005: registration stores immutable digest-addressed records,
// refuses invalid ones with every violation and stores nothing, is Club Admin only, is audited and counted, and never leaks content.
public sealed class RegistrationTests(PostgresContainerFixture postgres)
{
	private const string AgentId = "specialist-tactics";
	private const string Version = "1.0.0";

	private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? ETag)
	{
		public string[] Codes => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("violations", out var v)
			? [.. v.EnumerateArray().Select(x => x.GetProperty("code").GetString()!)]
			: [];
	}

	private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

	private static async Task<Reply> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using (response)
		{
			var text = await response.Content.ReadAsStringAsync(ct);
			var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
			return new Reply(response.StatusCode, body, response.Headers.ETag?.Tag);
		}
	}

	private static async Task<Reply> RegisterAsync(AgentCatalogApi api, CatalogDocument artifact, CancellationToken ct) =>
		await ReadAsync(await api.RegisterArtifactAsync(artifact, ct), ct);

	private static async Task<Reply> RegisterVersionAsync(AgentCatalogApi api, JsonNode document, CancellationToken ct) =>
		await ReadAsync(await api.RegisterAgentVersionAsync(Version, document, ct), ct);

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

	private static Task<long> AuditAsync(AgentOrchestrationFixture f, string eventType, string outcome, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}' AND outcome = '{outcome}'", ct);

	private static CatalogDocument Profile() => CatalogFixtures.Artifact(CatalogKinds.PhilosophyProfiles, "coach-jose-mourinho-1.0.0");

	private static CatalogDocument[] Valid() =>
	[
		CatalogFixtures.Artifact(CatalogKinds.PromptTemplates, "specialist-tactics-prompt"),
		CatalogFixtures.Artifact(CatalogKinds.ModelSelections, "simulated-minimal"),
		CatalogFixtures.Artifact(CatalogKinds.ToolPolicies, "specialist-tactics-tools"),
		CatalogFixtures.Artifact(CatalogKinds.SafetyPolicies, "specialist-safety"),
		CatalogFixtures.Artifact(CatalogKinds.DimensionSets, "coach-philosophy-dimensions-1.0.0"),
		Profile(),
		CatalogFixtures.Artifact(CatalogKinds.ModifierPolicies, "coach-modifier-policy-1.0.0"),
	];

	private static MeterListener Listen(List<Measurement> sink)
	{
		var listener = new MeterListener
		{
			InstrumentPublished = (instrument, l) =>
			{
				if (instrument.Meter.Name == AgentCatalogMetrics.MeterName)
				{
					l.EnableMeasurementEvents(instrument);
				}
			},
		};
		listener.SetMeasurementEventCallback<long>((i, v, t, _) =>
		{
			lock (sink)
			{
				sink.Add(new Measurement(i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value)));
			}
		});
		listener.Start();
		return listener;
	}

	private static long Registrations(List<Measurement> sink, string kind, string outcome)
	{
		lock (sink)
		{
			return sink.Where(m => m.Instrument == "agent_catalog.registrations"
					&& (string?)m.Tags.GetValueOrDefault("kind") == kind && (string?)m.Tags.GetValueOrDefault("outcome") == outcome)
				.Sum(m => m.Value);
		}
	}

	[Fact]
	public async Task EveryArtifactKindAndAnAgentVersionAreStoredWithTheDigestOfTheirDocument()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);

		foreach (var artifact in Valid())
		{
			var stored = await RegisterAsync(api, artifact, ct);
			stored.Status.ShouldBe(HttpStatusCode.Created, $"{artifact.Kind}: {stored.Body}");
			stored.Body.GetProperty("digest").GetString().ShouldBe(artifact.Digest);
			stored.Body.GetProperty("id").GetString().ShouldBe(artifact.Id);
			stored.Body.GetProperty("version").GetString().ShouldBe(artifact.Version);
		}

		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		var registered = await RegisterVersionAsync(api, CatalogFixtures.AgentVersion(artifacts), ct);

		registered.Status.ShouldBe(HttpStatusCode.Created, registered.Body.ToString());
		registered.Body.GetProperty("state").GetString().ShouldBe("draft");
		registered.Body.GetProperty("digest").GetString().ShouldStartWith("sha-256:");
		(await StoredAsync(f, ct)).ShouldBe(8);
		(await AuditAsync(f, "agent.catalog.record.registered", "succeeded", ct)).ShouldBe(8);
	}

	[Fact]
	public async Task EqualReplaysReturnTheStoredRecordAndDifferentContentIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		await api.RegisterAllAsync(artifacts.All, ct);
		var document = CatalogFixtures.AgentVersion(artifacts);
		var first = await RegisterVersionAsync(api, document, ct);

		var replay = await RegisterVersionAsync(api, document, ct);
		replay.Status.ShouldBe(HttpStatusCode.OK, replay.Body.ToString());
		replay.Body.GetProperty("digest").GetString().ShouldBe(first.Body.GetProperty("digest").GetString());
		replay.Body.GetProperty("document").GetRawText().ShouldBe(first.Body.GetProperty("document").GetRawText());

		var changed = (JsonObject)document.DeepClone();
		changed["displayName"] = "Another Name";
		ShouldBeProblem(await RegisterVersionAsync(api, changed, ct), HttpStatusCode.Conflict, "agent-catalog-version-exists");

		var model = artifacts.Model;
		var modelReplay = await RegisterAsync(api, model, ct);
		modelReplay.Status.ShouldBe(HttpStatusCode.OK, modelReplay.Body.ToString());
		modelReplay.Body.GetProperty("digest").GetString().ShouldBe(model.Digest);
		ShouldBeProblem(await RegisterAsync(api, model.Edit(d => d["model"] = "another-model"), ct), HttpStatusCode.Conflict, "agent-catalog-version-exists");

		(await StoredAsync(f, ct)).ShouldBe(5);
		(await AuditAsync(f, "agent.catalog.record.registered", "unchanged", ct)).ShouldBe(2);
		(await AuditAsync(f, "agent.catalog.record.registered", "refused", ct)).ShouldBe(2);
		(await GetStoredModelAsync(api, model, ct)).ShouldBe(model.Digest);
	}

	private static async Task<string?> GetStoredModelAsync(AgentCatalogApi api, CatalogDocument model, CancellationToken ct)
	{
		var read = await ReadAsync(await api.GetArtifactAsync(model.Kind, model.Id, model.Version, null, ct), ct);
		return read.Body.GetProperty("digest").GetString();
	}

	[Fact]
	public async Task InvalidRegistrationsNameEveryViolationAndStoreNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		var tools = CatalogFixtures.Artifact(CatalogKinds.ToolPolicies, "specialist-tactics-tools");

		ShouldBeRefused(await RegisterAsync(api, tools.Edit(d => ((JsonArray)d["tools"]!).Add("delete_everything")), ct));
		ShouldBeRefused(await RegisterAsync(api, artifacts.Model.Edit(d => d["apiKey"] = "secret"), ct));
		ShouldBeRefused(await RegisterAsync(api, artifacts.Model.Edit(d => d["model"] = "bad name@"), ct));
		ShouldBeRefused(await RegisterAsync(api, artifacts.Model.Edit(d => d["provider"] = "unknown-provider"), ct));

		var several = await RegisterAsync(api, artifacts.Model.Edit(d =>
		{
			d["apiKey"] = "secret";
			d["model"] = "bad name@";
			d["provider"] = "unknown-provider";
		}), ct);
		ShouldBeRefused(several);
		several.Codes.Length.ShouldBeGreaterThanOrEqualTo(3, several.Body.ToString());
		(await StoredAsync(f, ct)).ShouldBe(0);

		// Agent versions: missing artifacts, an identifier outside the catalog, and a group mismatch.
		ShouldBeRefused(await RegisterVersionAsync(api, CatalogFixtures.AgentVersion(artifacts), ct), "artifact-not-found");
		await api.RegisterAllAsync(artifacts.All, ct);
		var stored = await StoredAsync(f, ct);

		ShouldBeRefused(await RegisterVersionAsync(api, EditVersion(artifacts, d => d["agentId"] = "specialist-unknown"), ct));
		ShouldBeRefused(await RegisterVersionAsync(api, EditVersion(artifacts, d => d["agentId"] = "coach-jurgen-klopp"), ct), "group-mismatch");

		// A tool outside the baseline of the video analysis specialist.
		var outside = artifacts with
		{
			Tools = artifacts.Tools.Edit(d =>
			{
				d["id"] = "video-tools";
				d["tools"] = new JsonArray("get_match", "get_player_stats");
			}),
		};
		(await RegisterAsync(api, outside.Tools, ct)).Status.ShouldBe(HttpStatusCode.Created);
		var video = CatalogFixtures.AgentVersion(outside, "specialist-video-analysis-1.0.0");
		ShouldBeRefused(await RegisterVersionAsync(api, video, ct), "tool-outside-baseline");

		// A specialist must not reference a philosophy profile.
		await api.RegisterAllAsync([Valid()[4], Profile()], ct);
		var withProfile = CatalogFixtures.AgentVersion(artifacts);
		CatalogFixtures.Pin((JsonObject)withProfile["artifacts"]!, "philosophyProfile", Profile());
		ShouldBeRefused(await RegisterVersionAsync(api, withProfile, ct));

		(await StoredAsync(f, ct)).ShouldBe(stored + 3);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_versions", ct)).ShouldBe(0);
		(await AuditAsync(f, "agent.catalog.record.registered", "refused", ct)).ShouldBe(10);
	}

	private static JsonObject EditVersion(SpecialistArtifacts artifacts, Action<JsonObject> change)
	{
		var document = CatalogFixtures.AgentVersion(artifacts);
		change(document);
		return document;
	}

	[Fact]
	public async Task MembersWithoutTheClubAdminRoleAreRefusedAndAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		await TestMembers.SeedAsync(f.Db, "plain-member", cancellationToken: ct);
		using var member = await ApiSession.SignInAsync(f.Factory, "plain-member", TestMembers.DefaultPassword, ct);
		var api = new AgentCatalogApi(member);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();

		var replies = new[]
		{
			await ReadAsync(await api.GetBaselineAsync(ct), ct),
			await RegisterAsync(api, artifacts.Prompt, ct),
			await ReadAsync(await api.ListArtifactsAsync(CatalogKinds.PromptTemplates, null, ct), ct),
			await ReadAsync(await api.GetArtifactAsync(CatalogKinds.PromptTemplates, artifacts.Prompt.Id, Version, null, ct), ct),
			await RegisterVersionAsync(api, CatalogFixtures.AgentVersion(artifacts), ct),
			await ReadAsync(await api.ListAgentVersionsAsync(null, null, ct), ct),
			await ReadAsync(await api.GetAgentVersionAsync(AgentId, Version, null, ct), ct),
		};

		replies.ShouldAllBe(r => r.Status == HttpStatusCode.Forbidden);
		(await StoredAsync(f, ct)).ShouldBe(0);
		(await AuditAsync(f, "agent.catalog.access.refused", "denied", ct)).ShouldBe(replies.Length);
	}

	[Fact]
	public async Task ListAndReadReturnEachRecordWithItsDigestDocumentMemberAndTime()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		await api.RegisterAllAsync(artifacts.All, ct);
		await RegisterVersionAsync(api, CatalogFixtures.AgentVersion(artifacts), ct);

		var list = await ReadAsync(await api.ListArtifactsAsync(CatalogKinds.ToolPolicies, null, ct), ct);
		list.Status.ShouldBe(HttpStatusCode.OK);
		var item = list.Body.GetProperty("items").EnumerateArray().Single();
		item.GetProperty("id").GetString().ShouldBe(artifacts.Tools.Id);
		item.GetProperty("digest").GetString().ShouldBe(artifacts.Tools.Digest);
		Guid.TryParse(item.GetProperty("registeredBy").GetString(), out _).ShouldBeTrue();
		item.GetProperty("registeredAt").GetDateTimeOffset().ShouldNotBe(default);
		(await ReadAsync(await api.ListArtifactsAsync(CatalogKinds.ToolPolicies, "other-id", ct), ct))
			.Body.GetProperty("items").GetArrayLength().ShouldBe(0);

		using var read = await api.GetArtifactAsync(CatalogKinds.ToolPolicies, artifacts.Tools.Id, artifacts.Tools.Version, null, ct);
		var etag = read.Headers.ETag?.Tag;
		var artifact = JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct)).RootElement;
		artifact.GetProperty("digest").GetString().ShouldBe(artifacts.Tools.Digest);
		artifact.GetProperty("document").GetProperty("tools").GetArrayLength().ShouldBe(5);
		etag.ShouldNotBeNull();
		(await ReadAsync(await api.GetArtifactAsync(CatalogKinds.ToolPolicies, artifacts.Tools.Id, artifacts.Tools.Version, etag, ct), ct))
			.Status.ShouldBe(HttpStatusCode.NotModified);
		(await ReadAsync(await api.GetArtifactAsync(CatalogKinds.ToolPolicies, "missing", "1.0.0", null, ct), ct))
			.Status.ShouldBe(HttpStatusCode.NotFound);

		var versions = await ReadAsync(await api.ListAgentVersionsAsync(AgentId, "draft", ct), ct);
		var summary = versions.Body.GetProperty("items").EnumerateArray().Single();
		summary.GetProperty("state").GetString().ShouldBe("draft");
		summary.GetProperty("enabled").GetBoolean().ShouldBeFalse();
		summary.GetProperty("digest").GetString().ShouldStartWith("sha-256:");
		(await ReadAsync(await api.ListAgentVersionsAsync(AgentId, "approved", ct), ct))
			.Body.GetProperty("items").GetArrayLength().ShouldBe(0);

		var version = await ReadAsync(await api.GetAgentVersionAsync(AgentId, Version, null, ct), ct);
		version.Body.GetProperty("document").GetProperty("agentId").GetString().ShouldBe(AgentId);
		version.Body.GetProperty("state").GetString().ShouldBe("draft");
		version.Body.GetProperty("enabled").GetBoolean().ShouldBeFalse();
		Guid.TryParse(version.Body.GetProperty("registeredBy").GetString(), out _).ShouldBeTrue();
		version.Body.GetProperty("registeredAt").GetDateTimeOffset().ShouldNotBe(default);
		version.ETag.ShouldNotBeNull();
	}

	[Fact]
	public async Task RegistrationsAreCountedByKindAndOutcome()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var sink = new List<Measurement>();
		using var listener = Listen(sink);
		var api = new AgentCatalogApi(f.Admin);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();

		await api.RegisterAllAsync(artifacts.All, ct);
		await RegisterAsync(api, artifacts.Tools, ct);
		await RegisterAsync(api, artifacts.Tools.Edit(d => ((JsonArray)d["tools"]!).Add("delete_everything")), ct);
		await RegisterVersionAsync(api, CatalogFixtures.AgentVersion(artifacts), ct);

		// The meter is process-wide, so concurrent tests can only add to these lower bounds.
		Registrations(sink, "tool-policy", "accepted").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "tool-policy", "replayed").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "tool-policy", "refused").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "prompt-template", "accepted").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "model-selection", "accepted").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "safety-policy", "accepted").ShouldBeGreaterThanOrEqualTo(1);
		Registrations(sink, "agent-version", "accepted").ShouldBeGreaterThanOrEqualTo(1);
	}

	[Fact]
	public async Task AuditLogsAndTelemetryCarryNoPromptRationaleOrProvenanceText()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var sink = new List<Measurement>();
		using var listener = Listen(sink);
		var api = new AgentCatalogApi(f.Admin);
		string[] secrets = ["PRIVATE-PROMPT-5c1e", "PRIVATE-RATIONALE-91ab", "PRIVATE-PROVENANCE-3d07", "PRIVATE-KEY-0d9f"];
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		var prompt = artifacts.Prompt.Edit(d => d["text"] = d["text"]!.GetValue<string>() + "\n" + secrets[0]);
		var profile = Profile().Edit(d =>
		{
			var first = (JsonObject)((JsonObject)d["weights"]!).First().Value!;
			first["rationale"] = secrets[1];
			first["provenance"] = secrets[2];
		});

		await api.RegisterAllAsync([prompt, artifacts.Model, artifacts.Tools, artifacts.Safety, Valid()[4], profile], ct);
		await RegisterAsync(api, prompt, ct);
		ShouldBeRefused(await RegisterAsync(api, artifacts.Model.Edit(d => d["apiKey"] = secrets[3]), ct));
		ShouldBeRefused(await RegisterAsync(api, prompt.Edit(d => d["slots"] = new JsonArray("unknown_slot")), ct));

		var audit = await TextAsync(
			f, "SELECT coalesce(string_agg(event_type || action || outcome || coalesce(reason_code, '') || coalesce(resource_id, '') || details::text, ' '), '') FROM socalytics.security_audit_event", ct);
		var logs = string.Join(
			' ', f.Factory.Logs.Entries.Select(e => e.Message + " " + string.Join(' ', e.State.Select(s => s.Key + "=" + s.Value)) + " " + e.Exception));
		string telemetry;
		lock (sink)
		{
			telemetry = string.Join(' ', sink.SelectMany(m => m.Tags.Select(t => $"{t.Key}={t.Value}")));
		}

		audit.ShouldContain("agent.catalog.record.registered");
		foreach (var secret in secrets)
		{
			audit.ShouldNotContain(secret);
			logs.ShouldNotContain(secret);
			telemetry.ShouldNotContain(secret);
		}
	}

	private static async Task<string> TextAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}
}
