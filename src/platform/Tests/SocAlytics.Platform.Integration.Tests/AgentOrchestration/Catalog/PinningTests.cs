using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-019–FR-021, FR-023a, FR-023b, SC-001, SC-004: an invocation pins the enabled agent version and every artifact digest at submission and keeps them.
public sealed class PinningTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string SeedVersion = "0.1.0";
	private const string NewVersion = "1.0.0";
	private const string Question = "Which pressing triggers did the team miss?";

	// The workers are off so that only the calls of the test move time-dependent state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using (response)
		{
			var text = await response.Content.ReadAsStringAsync(ct);
			return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
		}
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

	private static async Task<Guid> ConversationAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var id = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/participants", new { agentId = Tactics }, created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		return id;
	}

	private static async Task<HttpResponseMessage> SubmitRawAsync(AgentOrchestrationFixture f, Guid conversationId, CancellationToken ct) =>
		await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = Question }, null, ct);

	private static async Task<Guid> SubmitAsync(AgentOrchestrationFixture f, Guid conversationId, CancellationToken ct)
	{
		using var response = await SubmitRawAsync(f, conversationId, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(ct));
		return (await ReadAsync(response, ct)).GetProperty("invocationId").GetGuid();
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static async Task<JsonElement> GetInvocationAsync(AgentOrchestrationFixture f, Guid invocationId, CancellationToken ct)
	{
		var response = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return await ReadAsync(response, ct);
	}

	private static async Task<JsonElement> GetLineageAsync(AgentOrchestrationFixture f, Guid invocationId, CancellationToken ct)
	{
		var response = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}/lineage", null, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return await ReadAsync(response, ct);
	}

	// Registers, approves, and enables the specialist's artifacts as version 1.0.0, which replaces the seeded version.
	private static async Task EnableNewVersionAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var api = new AgentCatalogApi(f.Admin);
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		using var registered = await api.RegisterSpecialistAsync(artifacts, NewVersion, ct);
		registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(ct));
		using var approved = await api.ApproveAgentVersionAsync(Tactics, NewVersion, registered.Headers.ETag!.Tag, null, ct);
		approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(ct));
		await SetEnabledAsync(f, NewVersion, ct);
	}

	private static async Task SetEnabledAsync(AgentOrchestrationFixture f, string? version, CancellationToken ct)
	{
		var list = await ReadAsync(await MemberAsync(f, HttpMethod.Get, "/api/v1/agent-catalog/enablements", null, null, ct), ct);
		var etag = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("agentId").GetString() == Tactics).GetProperty("etag").GetString()!;
		using var response = await MemberAsync(f, HttpMethod.Put, $"/api/v1/agent-catalog/enablements/{Tactics}", new { version }, etag, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
	}

	private static async Task RetireAsync(AgentOrchestrationFixture f, string version, CancellationToken ct)
	{
		var api = new AgentCatalogApi(f.Admin);
		using var read = await api.GetAgentVersionAsync(Tactics, version, null, ct);
		using var retired = await api.RetireAgentVersionAsync(Tactics, version, read.Headers.ETag!.Tag, ct);
		retired.StatusCode.ShouldBe(HttpStatusCode.OK, await retired.Content.ReadAsStringAsync(ct));
	}

	private static async Task ExpireAndRequeueAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
		await using var scope = f.Factory.Services.CreateAsyncScope();
		var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
		result.Requeued.ShouldBe(1);
	}

	private static async Task<string> DigestAsync(AgentOrchestrationFixture f, string version, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT digest FROM socalytics.agent_versions WHERE agent_id = @agent AND agent_version = @version", connection);
		command.Parameters.AddWithValue("agent", Tactics);
		command.Parameters.AddWithValue("version", version);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	private static void ShouldNotCarryContent(JsonElement pin)
	{
		pin.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["artifacts", "digest", "version"]);
		foreach (var artifact in pin.GetProperty("artifacts").EnumerateArray())
		{
			artifact.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["digest", "id", "kind", "version"]);
		}
	}

	[Fact]
	public async Task SubmissionPinsTheEnabledVersionAndEveryArtifactDigestWithoutContent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var conversation = await ConversationAsync(f, ct);

		var invocationId = await SubmitAsync(f, conversation, ct);

		var invocation = await GetInvocationAsync(f, invocationId, ct);
		var pin = invocation.GetProperty("agent");
		ShouldNotCarryContent(pin);
		pin.GetProperty("version").GetString().ShouldBe(SeedVersion);
		pin.GetProperty("digest").GetString().ShouldBe(await DigestAsync(f, SeedVersion, ct));
		var artifacts = pin.GetProperty("artifacts").EnumerateArray().ToList();
		artifacts.Select(a => a.GetProperty("kind").GetString()).Order()
			.ShouldBe(["model-selection", "prompt-template", "safety-policy", "tool-policy"]);

		foreach (var artifact in artifacts)
		{
			(await f.ScalarAsync(
				$"""
				SELECT count(*) FROM socalytics.agent_invocation_pins p
				JOIN socalytics.agent_catalog_artifacts a ON a.kind = p.kind AND a.artifact_id = p.artifact_id AND a.artifact_version = p.artifact_version
				WHERE p.invocation_id = '{invocationId}' AND p.kind = '{artifact.GetProperty("kind").GetString()}'
				  AND p.artifact_id = '{artifact.GetProperty("id").GetString()}' AND p.artifact_version = '{artifact.GetProperty("version").GetString()}'
				  AND p.digest = '{artifact.GetProperty("digest").GetString()}' AND a.digest = p.digest
				""", ct)).ShouldBe(1);
		}

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocationId}' AND agent_version = '{SeedVersion}' AND agent_version_digest = '{pin.GetProperty("digest").GetString()}'",
			ct)).ShouldBe(1);

		var lineage = await GetLineageAsync(f, invocationId, ct);
		lineage.GetProperty("agent").GetRawText().ShouldBe(pin.GetRawText());
	}

	[Fact]
	public async Task PinsSurviveEnablementChangesDisablingRetirementAndRetries()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var conversation = await ConversationAsync(f, ct);
		var queued = await SubmitAsync(f, conversation, ct);
		var pinned = (await GetInvocationAsync(f, queued, ct)).GetProperty("agent").GetRawText();

		// A new version replaces the enabled one: the queued invocation keeps its pin, a new submission pins the new version.
		await EnableNewVersionAsync(f, ct);
		var next = await SubmitAsync(f, conversation, ct);
		var nextPin = (await GetInvocationAsync(f, next, ct)).GetProperty("agent");
		nextPin.GetProperty("version").GetString().ShouldBe(NewVersion);
		nextPin.GetProperty("digest").GetString().ShouldBe(await DigestAsync(f, NewVersion, ct));
		(await GetInvocationAsync(f, queued, ct)).GetProperty("agent").GetRawText().ShouldBe(pinned);

		// Disabling the agent does not change the claim's envelope of the queued invocation.
		await SetEnabledAsync(f, null, ct);
		var first = await ClaimAsync(f, token, queued, ct);
		var firstPin = first.GetProperty("agent");
		firstPin.GetProperty("version").GetString().ShouldBe(SeedVersion);
		firstPin.GetProperty("digest").GetString().ShouldBe(await DigestAsync(f, SeedVersion, ct));
		firstPin.GetProperty("artifacts").GetRawText().ShouldBe(JsonDocument.Parse(pinned).RootElement.GetProperty("artifacts").GetRawText());

		// After the lease expired and the pinned version was retired, the retry keeps the same pins.
		await ExpireAndRequeueAsync(f, ct);
		await RetireAsync(f, SeedVersion, ct);
		var second = await ClaimAsync(f, token, queued, ct);
		second.GetProperty("agent").GetRawText().ShouldBe(first.GetProperty("agent").GetRawText());

		(await GetInvocationAsync(f, queued, ct)).GetProperty("agent").GetRawText().ShouldBe(pinned);
		(await GetLineageAsync(f, queued, ct)).GetProperty("agent").GetRawText().ShouldBe(pinned);

		// Without an enabled version a new submission is refused.
		using var refused = await SubmitRawAsync(f, conversation, ct);
		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await ReadAsync(refused, ct)).GetProperty("code").GetString().ShouldBe("agent-not-available");
	}

	[Fact]
	public async Task AnUnpinnedInvocationIsRefusedAtClaimAndFailsAndStaysReadable()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var conversation = await ConversationAsync(f, ct);
		var invocationId = await SubmitAsync(f, conversation, ct);

		// The state of an invocation submitted before pins existed.
		await using (var connection = new NpgsqlConnection(f.Db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand(
				$"""
				ALTER TABLE socalytics.agent_invocation_pins DISABLE TRIGGER trg_agent_invocation_pins_immutable;
				DELETE FROM socalytics.agent_invocation_pins WHERE invocation_id = '{invocationId}';
				ALTER TABLE socalytics.agent_invocation_pins ENABLE TRIGGER trg_agent_invocation_pins_immutable;
				UPDATE socalytics.agent_invocations SET agent_version = NULL, agent_version_digest = NULL WHERE id = '{invocationId}';
				""", connection);
			await command.ExecuteNonQueryAsync(ct);
		}

		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await ReadAsync(claim, ct)).GetProperty("code").GetString().ShouldBe("claim-obsolete");

		var invocation = await GetInvocationAsync(f, invocationId, ct);
		invocation.GetProperty("state").GetString().ShouldBe("failed");
		invocation.GetProperty("failureReason").GetString().ShouldBe("agent-not-available");
		(invocation.TryGetProperty("agent", out var agent) ? agent.ValueKind : JsonValueKind.Null).ShouldBe(JsonValueKind.Null);

		var lineage = await GetLineageAsync(f, invocationId, ct);
		lineage.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ShouldContain("failed");
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}'", ct)).ShouldBe(0);
	}
}
