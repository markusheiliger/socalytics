using Microsoft.AspNetCore.Authentication;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;

namespace SocAlytics.Platform.Api.AgentOrchestration;

internal static class AgentWorkloadEndpoints
{
	public static IServiceCollection AddAgentWorkloadSecurity(this IServiceCollection services)
	{
		services.AddAuthentication()
			.AddScheme<AuthenticationSchemeOptions, AgentWorkloadDPoPAuthenticationHandler>(
				AgentWorkloadDPoPDefaults.AuthenticationScheme, null);
		services.AddAuthorizationBuilder()
			.AddPolicy(AuthorizationPolicyNames.AgentWorkload, policy => policy
				.AddAuthenticationSchemes(AgentWorkloadDPoPDefaults.AuthenticationScheme)
				.RequireAuthenticatedUser()
				.RequireClaim(AgentWorkloadClaimTypes.Scope, AgentWorkloadDPoPDefaults.ScopeValue));
		return services;
	}

	public static IEndpointRouteBuilder MapAgentWorkloadEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/agent-orchestration/workload/token", IssueTokenAsync)
			.AllowAnonymous()
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.RequireRateLimiting(RegistryRateLimiting.Token)
			.WithTags("Agent workload")
			.WithName("issueAgentWorkloadToken");
		return routes;
	}

	private static async Task<IResult> IssueTokenAsync(
		HttpContext http, IssueAgentWorkloadTokenHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.Pragma = "no-cache";
		if (!http.Request.HasFormContentType)
		{
			return Error("invalid_request");
		}

		var form = await http.Request.ReadFormAsync(cancellationToken);
		if (form.Any(kv => kv.Value.Count > 1))
		{
			return Error("invalid_request");
		}

		string? Single(string key) => form.TryGetValue(key, out var v) && v.Count == 1 ? v[0] : null;
		var dpop = http.Request.Headers["DPoP"];
		var result = await handler.HandleAsync(
			new IssueAgentWorkloadTokenCommand(
				Single("grant_type"), Single("client_assertion_type"), Single("client_assertion"), Single("client_id"),
				Single("scope"), dpop.Count == 1 ? dpop[0] : null),
			cancellationToken);
		if (!result.Succeeded)
		{
			return Error(result.Error!);
		}

		return Results.Json(new
		{
			access_token = result.AccessToken,
			token_type = "DPoP",
			expires_in = result.ExpiresIn,
			scope = AgentWorkloadTokenResult.Scope,
		});
	}

	private static IResult Error(string error) =>
		Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
}
