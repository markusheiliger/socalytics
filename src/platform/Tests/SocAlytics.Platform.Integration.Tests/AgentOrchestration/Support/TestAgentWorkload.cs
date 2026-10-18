using System.Net.Http.Json;
using System.Text.Json;
using SocAlytics.Platform.Integration.Tests.Registry.Support;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;

// In-memory ECDSA P-256 workload key that builds client assertions and DPoP proofs; claims can be overridden for negative cases.
internal sealed class TestAgentWorkload : IDisposable
{
	public const string Origin = "https://localhost";
	public const string TokenPath = "/api/v1/agent-orchestration/workload/token";
	public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

	private readonly TestAnalystManager _key;

	public TestAgentWorkload(string clientId = "workload-1")
	{
		ClientId = clientId;
		_key = new TestAnalystManager(clientId);
	}

	public string ClientId { get; }

	public string PublicJwk => _key.PublicJwk;

	public string Thumbprint => _key.Thumbprint;

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

	public Task<HttpResponseMessage> CompleteAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/completion", report, ct);

	public Task<HttpResponseMessage> FailAsync(
		HttpClient client, DateTimeOffset now, string? token, Guid attemptId, object report, CancellationToken ct) =>
		SendAsync(client, now, token, HttpMethod.Post, $"/api/v1/agent-orchestration/attempts/{attemptId}/failure", report, ct);

	public void Dispose() => _key.Dispose();
}
