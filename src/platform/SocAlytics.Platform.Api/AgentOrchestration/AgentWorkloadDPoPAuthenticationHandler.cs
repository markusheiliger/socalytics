using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;

namespace SocAlytics.Platform.Api.AgentOrchestration;

public static class AgentWorkloadDPoPDefaults
{
	public const string AuthenticationScheme = "AgentWorkloadDPoP";
	public const string ScopeValue = "agent-workload";
}

public static class AgentWorkloadClaimTypes
{
	public const string ClientId = "socalytics:agent_workload_client_id";
	public const string Scope = "scope";
}

internal sealed class AgentWorkloadDPoPAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	private const string Prefix = "DPoP ";

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.IsHttps)
		{
			return Fail(AgentWorkloadAuthenticationResult.InvalidToken);
		}

		var authorization = Request.Headers.Authorization;
		if (authorization.Count != 1 || authorization[0] is not { } value
			|| !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || value.Length == Prefix.Length)
		{
			return Fail(AgentWorkloadAuthenticationResult.InvalidToken);
		}

		var dpop = Request.Headers["DPoP"];
		var handler = Context.RequestServices.GetRequiredService<AuthenticateAgentWorkloadRequestHandler>();
		var result = await handler.HandleAsync(
			new AuthenticateAgentWorkloadCommand(
				value[Prefix.Length..].Trim(), dpop.Count == 1 ? dpop[0] : null, Request.Method, Request.Path.Value ?? "/"),
			Context.RequestAborted);
		if (!result.Succeeded)
		{
			return Fail(result.Error!);
		}

		var identity = new ClaimsIdentity(
			[
				new Claim(AgentWorkloadClaimTypes.ClientId, result.Principal!.ClientId),
				new Claim(AgentWorkloadClaimTypes.Scope, AgentWorkloadDPoPDefaults.ScopeValue),
			],
			Scheme.Name);
		return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
	}

	protected override Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		var error = Context.Items.TryGetValue(typeof(AgentWorkloadDPoPAuthenticationHandler), out var e) && e is string s
			? s : AgentWorkloadAuthenticationResult.InvalidToken;
		Response.StatusCode = StatusCodes.Status401Unauthorized;
		Response.Headers.WWWAuthenticate = $"DPoP error=\"{error}\"";
		return Task.CompletedTask;
	}

	private AuthenticateResult Fail(string error)
	{
		Context.Items[typeof(AgentWorkloadDPoPAuthenticationHandler)] = error;
		return AuthenticateResult.Fail(error);
	}
}
