using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Integration.Tests.Registry.Support;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;

// In-memory ECDSA P-256 workload key that builds client assertions and DPoP proofs; claims can be overridden for negative cases.
internal sealed class TestAgentWorkload : IDisposable
{
	public const string Origin = "https://localhost";
	public const string TokenPath = "/api/v1/agent-orchestration/workload/token";
	public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

	private readonly TestAnalystManager _key;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Dictionary<string, string>> _pinnedDigests = new();

	public TestAgentWorkload(string clientId = "workload-1")
	{
		ClientId = clientId;
		_key = new TestAnalystManager(clientId);
	}

	public string ClientId { get; }

	public string PublicJwk => _key.PublicJwk;

	public string Thumbprint => _key.Thumbprint;

	public string PrivateKey => _key.PrivateKeyBase64;

	public string Assertion(
		DateTimeOffset now, string? clientId = null, string? jti = null, Action<Dictionary<string, object>>? claims = null) =>
		_key.ClientAssertion(now, Origin + TokenPath, clientId ?? ClientId, jti: jti, claims: claims);

	public string Proof(
		DateTimeOffset now, string method, string path, string? accessToken = null, string? jti = null,
		Action<Dictionary<string, object>>? claims = null) =>
		_key.DPoPProof(now, method, Origin + path, accessToken, jti, claims);

	// The assertion and the proof can be signed by different keys; the client id can be overridden.
	public HttpRequestMessage TokenRequest(
		DateTimeOffset now, string? clientId = null, string grantType = "client_credentials", string? scope = "agent-workload",
		string? proofJti = null, TestAgentWorkload? assertionBy = null, TestAgentWorkload? proofBy = null,
		Action<Dictionary<string, object>>? assertionClaims = null, string? assertion = null, string? proof = null, bool omitProof = false)
	{
		var id = clientId ?? ClientId;
		var form = new Dictionary<string, string>
		{
			["grant_type"] = grantType,
			["client_assertion_type"] = AssertionType,
			["client_assertion"] = assertion ?? (assertionBy ?? this).Assertion(now, id, claims: assertionClaims),
			["client_id"] = id,
		};
		if (scope is not null)
		{
			form["scope"] = scope;
		}

		var request = new HttpRequestMessage(HttpMethod.Post, TokenPath) { Content = new FormUrlEncodedContent(form) };
		if (!omitProof)
		{
			request.Headers.Add("DPoP", proof ?? (proofBy ?? this).Proof(now, "POST", TokenPath, jti: proofJti));
		}

		return request;
	}

	public Task<HttpResponseMessage> ObtainTokenAsync(HttpClient client, DateTimeOffset now, CancellationToken ct) =>
		client.SendAsync(TokenRequest(now), ct);

	public async Task<string> AccessTokenAsync(HttpClient client, DateTimeOffset now, CancellationToken ct)
	{
		using var response = await ObtainTokenAsync(client, now, ct);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return body.GetProperty("access_token").GetString()!;
	}

	public HttpRequestMessage Request(
		DateTimeOffset now, string? token, HttpMethod method, string path, object? body = null, string? proofJti = null,
		TestAgentWorkload? proofBy = null)
	{
		var request = new HttpRequestMessage(method, path);
		if (token is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + token);
		}

		request.Headers.Add("DPoP", (proofBy ?? this).Proof(now, method.Method, path, token, proofJti));
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		return request;
	}

	public Task<HttpResponseMessage> SendAsync(
		HttpClient client, DateTimeOffset now, string? token, HttpMethod method, string path, object? body, CancellationToken ct) =>
		client.SendAsync(Request(now, token, method, path, body), ct);

	public Task<HttpResponseMessage> IssueQueueCredentialsAsync(
		HttpClient client, DateTimeOffset now, string? token, object userPublicKey, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, "/api/v1/agent-orchestration/workload/queue-credentials", new { userPublicKey }, ct);

	public Task<HttpResponseMessage> ClaimAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid invocationId, string operationId, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/invocations/{invocationId}/claims", new { operationId }, ct);

	public Task<HttpResponseMessage> RenewLeaseAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, long fencingToken, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/lease", new { fencingToken }, ct);

	public Task<HttpResponseMessage> CheckpointAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/checkpoints", report, ct);

	public Task<HttpResponseMessage> ReadPinnedArtifactsAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, string fencingToken, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/pinned-artifacts", new { fencingToken }, ct);

	// By default the report's prompt, tool policy, and safety policy references are replaced by the pinned digests read as the
	// attempt; a report whose attempt cannot read them (stale, unknown) is sent unchanged.
	public async Task<HttpResponseMessage> CompleteAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct,
		bool pinnedReferences = true)
	{
		if (pinnedReferences && await WithPinnedReferencesAsync(client, now, token, attemptId, report, ct) is { } pinned)
		{
			report = pinned;
		}

		return await SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/completion", report, ct);
	}

	private async Task<JsonNode?> WithPinnedReferencesAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct)
	{
		if (JsonSerializer.SerializeToNode(report) is not JsonObject node
			|| node["fencingToken"]?.ToString() is not { } fencingToken
			|| node["executionReferences"] is not JsonObject references)
		{
			return null;
		}

		// Replays of an ended attempt reuse the digests read while it was current, so the request content stays identical.
		if (!_pinnedDigests.TryGetValue(attemptId, out var digests))
		{
			using var response = await ReadPinnedArtifactsAsync(client, now, token, attemptId, fencingToken, ct);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			digests = body.GetProperty("artifacts").EnumerateArray()
				.ToDictionary(a => a.GetProperty("kind").GetString()!, a => a.GetProperty("digest").GetString()!);
			_pinnedDigests[attemptId] = digests;
		}

		foreach (var (property, kind) in new[] { ("promptRef", "prompt-template"), ("toolPolicyRef", "tool-policy"), ("safetyPolicyRef", "safety-policy") })
		{
			if (digests.TryGetValue(kind, out var digest))
			{
				references[property] = digest;
			}
		}

		return node;
	}

	public Task<HttpResponseMessage> FailAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/failure", report, ct);

	public static string ToolPath(Guid attemptId, string pathSegment) =>
		$"/api/v1/agent-tools/attempts/{attemptId}/{pathSegment}";

	// Calls any allowlisted tool operation (path segment such as "get-match") as the attempt; arguments default to an empty object.
	public Task<HttpResponseMessage> CallToolAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, string pathSegment, string fencingToken, string callId,
		object? arguments, CancellationToken ct) =>
		SendAsync(
			client, now, token, HttpMethod.Post, ToolPath(attemptId, pathSegment),
			new { callId, fencingToken, arguments = arguments ?? new { } }, ct);

	// Calls a tool and reads the answered `200` response; any other status fails the call with the response body.
	public async Task<AgentToolResponse> CallToolAnsweredAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, string pathSegment, string fencingToken, string callId,
		object? arguments, CancellationToken ct)
	{
		using var response = await CallToolAsync(client, now, token, attemptId, pathSegment, fencingToken, callId, arguments, ct);
		var content = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Tool call '{pathSegment}' answered {(int)response.StatusCode}: {content}", null, response.StatusCode);
		}

		var body = JsonSerializer.Deserialize<JsonElement>(content);
		return new AgentToolResponse(
			body.GetProperty("callId").GetString()!,
			body.GetProperty("toolId").GetString()!,
			body.GetProperty("outcome").GetString()!,
			body.GetProperty("result").Clone());
	}

	public void Dispose() => _key.Dispose();
}

internal sealed record AgentToolResponse(string CallId, string ToolId, string Outcome, JsonElement Result);
