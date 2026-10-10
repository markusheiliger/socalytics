using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record RedeemCredentialRequest(string? AccountName, string? Credential, string? NewPassword);

internal static class CredentialEndpoints
{
	public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/credentials/redeem", RedeemAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("redeemCredential")
			.WithTags("Credentials")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		return routes;
	}

	private static async Task<IResult> RedeemAsync(HttpContext http, RedeemCredentialHandler handler, CancellationToken cancellationToken)
	{
		RedeemCredentialRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<RedeemCredentialRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new RedeemCredentialCommand(body?.AccountName, body?.Credential, body?.NewPassword), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}
}
