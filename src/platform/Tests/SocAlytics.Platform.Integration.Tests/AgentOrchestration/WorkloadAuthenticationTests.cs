using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Api.AgentOrchestration;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class WorkloadAuthenticationTests(PostgresContainerFixture postgres)
{
	private const string ProbePath = "/api/v1/agent-orchestration/workload/queue-credentials";
	private const string ManagerOperation = "/api/v1/analyst-managers/self/registration";
	private const string MemberOperation = "/api/v1/analyst-manager-registrations";

	private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
		(response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone());

	private static async Task<List<(string Outcome, string ActorKind, string? Resource, string? Reason)>> RefusalsAsync(
		AgentOrchestrationFixture f, string eventType, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT outcome, actor_kind, resource_id, reason_code FROM socalytics.security_audit_event WHERE event_type = @t ORDER BY occurred_at", connection);
		command.Parameters.AddWithValue("t", eventType);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var rows = new List<(string, string, string?, string?)>();
		while (await reader.ReadAsync(ct))
		{
			rows.Add((reader.GetString(0), reader.GetString(1),
				reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
		}

		return rows;
	}

	// Runs the workload scheme against a synthetic request, as the policy of every workload operation does.
	private static async Task<AuthenticateResult> AuthenticateAsync(
		AgentOrchestrationFixture f, string? authorization, string? proof, string method = "POST", string path = ProbePath, string? cookie = null)
	{
		await using var scope = f.Factory.Services.CreateAsyncScope();
		var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
		context.Request.Scheme = "https";
		context.Request.Host = new HostString("localhost");
		context.Request.Method = method;
		context.Request.Path = path;
		if (authorization is not null)
		{
			context.Request.Headers.Authorization = authorization;
		}

		if (proof is not null)
		{
			context.Request.Headers["DPoP"] = proof;
		}

		if (cookie is not null)
		{
			context.Request.Headers.Cookie = cookie;
		}

		return await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
			.AuthenticateAsync(context, AgentWorkloadDPoPDefaults.AuthenticationScheme);
	}

	[Fact]
	public async Task TokenIsIssuedForValidAssertionAndProof()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		using var response = await f.Workload.ObtainTokenAsync(f.Anon, f.Now, ct);
		var (status, body) = await ReadAsync(response, ct);
		status.ShouldBe(HttpStatusCode.OK, body.ToString());
		body.GetProperty("token_type").GetString().ShouldBe("DPoP");
		body.GetProperty("scope").GetString().ShouldBe("agent-workload");
		body.GetProperty("expires_in").GetInt32().ShouldBe(300);
		body.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();

		var issued = await RefusalsAsync(f, "agent-workload.credential-issued", ct);
		issued.ShouldHaveSingleItem().ShouldBe(("succeeded", "agent-workload", f.Workload.ClientId, null));

		var token = body.GetProperty("access_token").GetString()!;
		var result = await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token));
		result.Succeeded.ShouldBeTrue();
		result.Principal!.FindFirst(AgentWorkloadClaimTypes.ClientId)!.Value.ShouldBe(f.Workload.ClientId);
		result.Principal.FindFirst(AgentWorkloadClaimTypes.Scope)!.Value.ShouldBe("agent-workload");

		var authorization = f.Factory.Services.GetRequiredService<IAuthorizationService>();
		(await authorization.AuthorizeAsync(result.Principal, "AgentWorkload")).Succeeded.ShouldBeTrue();
	}

	[Fact]
	public async Task InvalidCredentialsAreRefusedAndAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		using var foreign = new TestAgentWorkload("workload-1");
		var now = f.Now;
		var attempts = new (string Name, Func<HttpRequestMessage> Build, string Resource, string Reason)[]
		{
			("invalid-assertion", () => f.Workload.TokenRequest(now, assertionBy: foreign), f.Workload.ClientId, "proof-invalid"),
			("foreign-dpop-key", () => f.Workload.TokenRequest(now, proofBy: foreign), f.Workload.ClientId, "proof-invalid"),
			("unknown-client", () => f.Workload.TokenRequest(now, clientId: "nobody"), "nobody", "unknown-client"),
			("disabled-client", () => f.DisabledWorkload.TokenRequest(now), AgentOrchestrationFixture.DisabledClientId, "client-disabled"),
		};
		foreach (var (name, build, _, _) in attempts)
		{
			using var response = await f.Anon.SendAsync(build(), ct);
			var (status, body) = await ReadAsync(response, ct);
			status.ShouldBe(HttpStatusCode.BadRequest, name);
			body.GetProperty("error").GetString().ShouldBe(name == "foreign-dpop-key" ? "invalid_dpop_proof" : "invalid_client", name);
			body.TryGetProperty("access_token", out _).ShouldBeFalse(name);
		}

		using var first = await f.Anon.SendAsync(f.Workload.TokenRequest(now, proofJti: "replayed"), ct);
		first.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var replay = await f.Anon.SendAsync(f.Workload.TokenRequest(now, proofJti: "replayed"), ct);
		var (replayStatus, replayBody) = await ReadAsync(replay, ct);
		replayStatus.ShouldBe(HttpStatusCode.BadRequest);
		replayBody.GetProperty("error").GetString().ShouldBe("invalid_dpop_proof");

		var refused = await RefusalsAsync(f, "agent-workload.credential-refused", ct);
		refused.Count.ShouldBe(attempts.Length + 1);
		refused.ShouldAllBe(r => r.Outcome == "refused" && r.ActorKind == "agent-workload");
		refused.Select(r => (r.Resource, r.Reason)).ShouldBe(
			[.. attempts.Select(a => ((string?)a.Resource, (string?)a.Reason)), (f.Workload.ClientId, "proof-replayed")]);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_workload_access_tokens", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task OAuthRequestErrorsAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		using var grant = await f.Anon.SendAsync(f.Workload.TokenRequest(f.Now, grantType: "password"), ct);
		(await ReadAsync(grant, ct)).Body.GetProperty("error").GetString().ShouldBe("unsupported_grant_type");
		using var scope = await f.Anon.SendAsync(f.Workload.TokenRequest(f.Now, scope: "analyst-manager"), ct);
		(await ReadAsync(scope, ct)).Body.GetProperty("error").GetString().ShouldBe("invalid_scope");
		using var noProof = await f.Anon.SendAsync(f.Workload.TokenRequest(f.Now, omitProof: true), ct);
		(await ReadAsync(noProof, ct)).Body.GetProperty("error").GetString().ShouldBe("invalid_dpop_proof");
	}

	[Fact]
	public async Task DisablingAClientStopsItsIssuedTokensAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token))).Succeeded.ShouldBeTrue();

		f.SetClientEnabled(f.Workload.ClientId, false);
		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token))).Succeeded.ShouldBeFalse();
		using var refused = await f.Anon.SendAsync(f.Workload.TokenRequest(f.Now), ct);
		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		f.SetClientEnabled(f.Workload.ClientId, true);
		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token))).Succeeded.ShouldBeTrue();
	}

	[Fact]
	public async Task RequestsWithBadTokenOrProofAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		using var foreign = new TestAgentWorkload();
		var cases = new (string Name, string? Authorization, string? Proof)[]
		{
			("no-token", null, f.Workload.Proof(f.Now, "POST", ProbePath, token)),
			("unknown-token", "DPoP unknown", f.Workload.Proof(f.Now, "POST", ProbePath, "unknown")),
			("no-proof", "DPoP " + token, null),
			("wrong-path", "DPoP " + token, f.Workload.Proof(f.Now, "POST", "/other", token)),
			("wrong-method", "DPoP " + token, f.Workload.Proof(f.Now, "GET", ProbePath, token)),
			("no-ath", "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath)),
			("foreign-key", "DPoP " + token, foreign.Proof(f.Now, "POST", ProbePath, token)),
		};
		foreach (var (name, authorization, proof) in cases)
		{
			(await AuthenticateAsync(f, authorization, proof)).Succeeded.ShouldBeFalse(name);
		}

		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token, "fixed"))).Succeeded.ShouldBeTrue();
		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token, "fixed"))).Succeeded.ShouldBeFalse();

		f.Factory.Time.Advance(TimeSpan.FromMinutes(6));
		(await AuthenticateAsync(f, "DPoP " + token, f.Workload.Proof(f.Now, "POST", ProbePath, token))).Succeeded.ShouldBeFalse();
	}

	[Fact]
	public async Task MemberSessionsAndManagerTokensDoNotSatisfyWorkloadOperations()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var (manager, managerToken) = await f.ActivateManagerAsync(ct);
		using var _ = manager;

		(await AuthenticateAsync(f, null, null, cookie: "unused=1")).Succeeded.ShouldBeFalse();
		(await AuthenticateAsync(f, "DPoP " + managerToken, manager.DPoPProof(f.Now, "POST", TestAgentWorkload.Origin + ProbePath, managerToken))).Succeeded.ShouldBeFalse();

		// Workload operations beyond the token operation arrive with later tasks; the scheme and policy are what gate them.
		using var memberCall = await f.Admin.SendAsync(HttpMethod.Post, ProbePath, new { userPublicKey = "x" }, ct);
		memberCall.StatusCode.ShouldNotBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task WorkloadTokensDoNotSatisfyMemberOrManagerOperations()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);

		using var manager = await f.Anon.SendAsync(f.Workload.Request(f.Now, token, HttpMethod.Get, ManagerOperation), ct);
		manager.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		using var member = await f.Anon.SendAsync(f.Workload.Request(f.Now, token, HttpMethod.Get, MemberOperation), ct);
		member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		using var bearer = new HttpRequestMessage(HttpMethod.Get, MemberOperation);
		bearer.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
		using var bearerResponse = await f.Anon.SendAsync(bearer, ct);
		bearerResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}
}
