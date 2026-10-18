using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record RequestPasswordRecoveryRequest(string? Address);

internal sealed record CheckPasswordRecoveryRequest(string? Token);

internal sealed record CompletePasswordRecoveryRequest(string? Token, string? NewPassword);

internal sealed record PasswordRecoveryCheckDto(string AccountName);

internal static class PasswordRecoveryEndpoints
{
	private const string Tag = "Password recovery";

	public static IEndpointRouteBuilder MapPasswordRecoveryEndpoints(this IEndpointRouteBuilder routes)
	{
		var recovery = routes.MapGroup(MemberApiRouteGroup.Prefix + "/password-recovery");
		recovery.MapPost("/requests", RequestAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(PasswordRecoveryRateLimiting.RequestPolicy)
			.WithName("requestPasswordRecovery")
			.WithTags(Tag)
			.Produces(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		recovery.MapPost("/checks", CheckAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(PasswordRecoveryRateLimiting.TokenPolicy)
			.WithName("checkPasswordRecovery")
			.WithTags(Tag)
			.Produces<PasswordRecoveryCheckDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		recovery.MapPost("/completions", CompleteAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(PasswordRecoveryRateLimiting.TokenPolicy)
			.WithName("completePasswordRecovery")
			.WithTags(Tag)
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		return routes;
	}

	private static async Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			return await http.Request.ReadFromJsonAsync<T>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			return null;
		}
	}

	private static async Task<IResult> RequestAsync(
		HttpContext http,
		RequestPasswordRecoveryHandler handler,
		IOptions<PasswordRecoveryOptions> options,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var started = time.GetTimestamp();
		var body = await ReadBodyAsync<RequestPasswordRecoveryRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new RequestPasswordRecoveryCommand(body?.Address), cancellationToken);

		// Every answer, success or validation failure, takes at least the minimum duration so timing does not reveal whether an account exists.
		var remaining = options.Value.MinimumResponseDuration - time.GetElapsedTime(started);
		if (remaining > TimeSpan.Zero)
		{
			await Task.Delay(remaining, time, cancellationToken);
		}

		return result.IsSuccess ? Results.Accepted() : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CheckAsync(
		HttpContext http,
		CheckPasswordRecoveryHandler handler,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<CheckPasswordRecoveryRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new CheckPasswordRecoveryCommand(body?.Token), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new PasswordRecoveryCheckDto(result.Value.AccountName))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CompleteAsync(
		HttpContext http,
		CompletePasswordRecoveryHandler handler,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<CompletePasswordRecoveryRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new CompletePasswordRecoveryCommand(body?.Token, body?.NewPassword), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}
}
