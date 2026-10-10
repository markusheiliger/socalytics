using SocAlytics.Platform.Application.Registry.MachineAccess;

namespace SocAlytics.Platform.Api.Registry;

internal static class AnalystManagerTokenEndpoint
{
	public static IEndpointRouteBuilder MapAnalystManagerTokenEndpoint(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/analyst-managers/token", IssueAsync)
			.AllowAnonymous()
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.RequireRateLimiting(RegistryRateLimiting.Token)
			.WithTags("Analyst Manager pairing")
			.WithName("issueAnalystManagerAccessToken");
		return routes;
	}

	private static async Task<IResult> IssueAsync(HttpContext http, IssueManagerTokenHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.Pragma = "no-cache";
		if (!http.Request.HasFormContentType)
		{
			return Error("invalid_request");
		}

		var form = await http.Request.ReadFormAsync(cancellationToken);
		string? Single(string key) => form.TryGetValue(key, out var v) && v.Count == 1 ? v[0] : null;
		if (form.Any(kv => kv.Value.Count > 1))
		{
			return Error("invalid_request");
		}

		var dpop = http.Request.Headers["DPoP"];
		var result = await handler.HandleAsync(
			new IssueManagerTokenCommand(
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
			scope = ManagerTokenResult.Scope,
		});
	}

	private static IResult Error(string error) =>
		Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
}
