using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

public sealed class PinnedArtifactReadTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string ReadPath = "/api/v1/agent-orchestration/attempts/{0}/pinned-artifacts";

	// The workers are off so that only the handlers called by a test move time-dependent state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
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

	private sealed record Setup(string Token, Guid ConversationId);

	private static async Task<Setup> PrepareAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics },
			created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		return new Setup(token, conversationId);
	}

	private static async Task<JsonElement> SubmitAndClaimAsync(AgentOrchestrationFixture f, Setup s, CancellationToken ct)
	{
		using var submitted = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{s.ConversationId}/invocations",
			new { agentId = Tactics, question = "What should we change?" }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();
		return await ClaimAsync(f, s, invocationId, ct);
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, Setup s, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, s.Token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static Guid AttemptId(JsonElement envelope) => envelope.GetProperty("attemptId").GetGuid();

	private static string FencingToken(JsonElement envelope) => envelope.GetProperty("fencingToken").GetString()!;

	private static Task<HttpResponseMessage> ReadPinnedAsync(
		AgentOrchestrationFixture f, Setup s, JsonElement envelope, string? fencingToken, CancellationToken ct) =>
		f.Workload.ReadPinnedArtifactsAsync(f.Anon, f.Now, s.Token, AttemptId(envelope), fencingToken ?? FencingToken(envelope), ct);

	private static async Task AssertStaleAsync(HttpResponseMessage response, string disposition, CancellationToken ct)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			var body = await ReadAsync(response, ct);
			body.GetProperty("code").GetString().ShouldBe("agent-attempt-stale");
			body.GetProperty("disposition").GetString().ShouldBe(disposition);
		}
	}

	private static object Completion(string token, JsonObject references) => new
	{
		operationId = "op-" + Guid.NewGuid().ToString("N"),
		fencingToken = token,
		advice = "Press higher.",
		executionReferences = references,
	};

	private static JsonObject References(JsonElement pinned)
	{
		var digests = pinned.GetProperty("artifacts").EnumerateArray()
			.ToDictionary(a => a.GetProperty("kind").GetString()!, a => a.GetProperty("digest").GetString()!);
		return new JsonObject
		{
			["runtime"] = "test-runtime",
			["modelProvider"] = "test-provider",
			["model"] = "test-model",
			["deployment"] = "test-deployment",
			["promptRef"] = digests["prompt-template"],
			["toolPolicyRef"] = digests["tool-policy"],
			["safetyPolicyRef"] = digests["safety-policy"],
		};
	}

	private static string Digest(JsonElement document) => CatalogFixtures.DigestOf(JsonNode.Parse(document.GetRawText())!);

	[Fact]
	public async Task TheEnvelopeConformsAndTheCurrentAttemptReadsEveryPinnedDocumentWithMatchingDigests()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await SubmitAndClaimAsync(f, s, ct);

		var valid = f.Factory.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.AgentExecutionEnvelopeV1, envelope);
		valid.IsValid.ShouldBeTrue(string.Join(", ", valid.Errors.Select(e => $"{e.Field}:{e.Keyword}")));
		envelope.GetProperty("schemaVersion").GetString().ShouldBe("1.1.0");
		var agent = envelope.GetProperty("agent");
		agent.GetProperty("agentId").GetString().ShouldBe(Tactics);
		var pinnedArtifacts = agent.GetProperty("artifacts").EnumerateArray().ToList();
		pinnedArtifacts.Count.ShouldBeGreaterThanOrEqualTo(4);

		using var response = await ReadPinnedAsync(f, s, envelope, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		response.Headers.CacheControl?.NoStore.ShouldBeTrue();
		var body = await ReadAsync(response, ct);

		var version = body.GetProperty("agentVersion");
		version.GetProperty("agentId").GetString().ShouldBe(Tactics);
		version.GetProperty("version").GetString().ShouldBe(agent.GetProperty("version").GetString());
		version.GetProperty("digest").GetString().ShouldBe(agent.GetProperty("digest").GetString());
		// An agent version digest covers its version beside the record.
		var versionDigest = CatalogFixtures.DigestOf(new JsonObject
		{
			["agentVersion"] = version.GetProperty("version").GetString(),
			["record"] = JsonNode.Parse(version.GetProperty("document").GetRawText()),
		});
		versionDigest.ShouldBe(version.GetProperty("digest").GetString());

		var read = body.GetProperty("artifacts").EnumerateArray().ToList();
		read.Count.ShouldBe(pinnedArtifacts.Count);
		foreach (var artifact in read)
		{
			Digest(artifact.GetProperty("document")).ShouldBe(artifact.GetProperty("digest").GetString());
			pinnedArtifacts.ShouldContain(p =>
				p.GetProperty("kind").GetString() == artifact.GetProperty("kind").GetString()
				&& p.GetProperty("id").GetString() == artifact.GetProperty("id").GetString()
				&& p.GetProperty("version").GetString() == artifact.GetProperty("version").GetString()
				&& p.GetProperty("digest").GetString() == artifact.GetProperty("digest").GetString());
		}

		new[] { "prompt-template", "model-selection", "tool-policy", "safety-policy" }
			.ShouldBeSubsetOf(read.Select(a => a.GetProperty("kind").GetString()!));
	}

	[Fact]
	public async Task FencedExpiredAndReplacedAttemptsAreStale()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);

		using var submitted = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{s.ConversationId}/invocations",
			new { agentId = Tactics, question = "What should we change?" }, null, ct);
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();
		var envelope = await ClaimAsync(f, s, invocationId, ct);
		var token = FencingToken(envelope);

		await AssertStaleAsync(await ReadPinnedAsync(f, s, envelope, (long.Parse(token) + 1).ToString(), ct), "attempt-fenced", ct);

		// After the lease expired and before the monitor ran, the attempt is still current but expired.
		f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
		await AssertStaleAsync(await ReadPinnedAsync(f, s, envelope, null, ct), "lease-expired", ct);

		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			var result = await scope.ServiceProvider.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct);
			result.Expired.ShouldBe(1);
			result.Requeued.ShouldBe(1);
		}

		var second = await ClaimAsync(f, s, invocationId, ct);
		await AssertStaleAsync(await ReadPinnedAsync(f, s, envelope, null, ct), "attempt-not-current", ct);

		using var current = await ReadPinnedAsync(f, s, second, null, ct);
		current.StatusCode.ShouldBe(HttpStatusCode.OK, await current.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task MemberSessionsAndManagerTokensAreRefusedWithUnauthorized()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await SubmitAndClaimAsync(f, s, ct);
		var path = string.Format(ReadPath, AttemptId(envelope));
		var body = new { fencingToken = FencingToken(envelope) };

		using var member = await f.Admin.SendAsync(HttpMethod.Post, path, body, ct);
		member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		var (manager, managerToken) = await f.ActivateManagerAsync(ct);
		using var _ = manager;
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + managerToken);
		request.Headers.Add("DPoP", manager.DPoPProof(f.Now, "POST", TestAgentWorkload.Origin + path, managerToken));
		using var managerCall = await f.Anon.SendAsync(request, ct);
		managerCall.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using var anonymous = await f.Anon.PostAsJsonAsync(path, body, ct);
		anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Theory]
	[InlineData("promptRef")]
	[InlineData("toolPolicyRef")]
	[InlineData("safetyPolicyRef")]
	public async Task AMismatchingReferenceIsRefusedAndTheAttemptStaysActive(string property)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await SubmitAndClaimAsync(f, s, ct);
		var attemptId = AttemptId(envelope);
		var token = FencingToken(envelope);

		using var read = await ReadPinnedAsync(f, s, envelope, null, ct);
		var references = References(await ReadAsync(read, ct));
		var good = (JsonObject)references.DeepClone();
		references[property] = "sha-256:" + new string('0', 64);

		using (var refused = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, attemptId, Completion(token, references), ct, pinnedReferences: false))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await ReadAsync(refused, ct)).GetProperty("code").GetString().ShouldBe("execution-references-mismatch");
		}

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_accepted_advice", ct)).ShouldBe(0);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{envelope.GetProperty("invocationId").GetGuid()}' AND kind = 'completed'",
			ct)).ShouldBe(0);

		// The attempt is still current: its lease renews and it can complete with the pinned references.
		using (var renewed = await f.Workload.SendAsync(
			f.Anon, f.Now, s.Token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/lease", new { fencingToken = token }, ct))
		{
			renewed.StatusCode.ShouldBe(HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync(ct));
		}

		using var accepted = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, attemptId, Completion(token, good), ct, pinnedReferences: false);
		accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(ct));
		(await ReadAsync(accepted, ct)).GetProperty("invocationState").GetString().ShouldBe("succeeded");
	}

	[Fact]
	public async Task AMatchingCompletionRecordsThePins()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var s = await PrepareAsync(f, ct);
		var envelope = await SubmitAndClaimAsync(f, s, ct);
		var invocationId = envelope.GetProperty("invocationId").GetGuid();

		using var read = await ReadPinnedAsync(f, s, envelope, null, ct);
		var references = References(await ReadAsync(read, ct));

		using var completed = await f.Workload.CompleteAsync(
			f.Anon, f.Now, s.Token, AttemptId(envelope), Completion(FencingToken(envelope), references), ct, pinnedReferences: false);
		completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));

		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_accepted_advice", ct)).ShouldBe(1);
		foreach (var property in new[] { "promptRef", "toolPolicyRef", "safetyPolicyRef" })
		{
			var digest = references[property]!.GetValue<string>();
			(await f.ScalarAsync(
				$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'completed' AND payload ->> '{property}' = '{digest}'",
				ct)).ShouldBe(1, property);
		}
	}
}
