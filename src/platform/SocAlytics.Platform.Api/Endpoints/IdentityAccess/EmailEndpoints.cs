using System.Globalization;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess.Email;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record SetEmailAddressRequest(string? Address);

internal sealed record ConfirmEmailRequest(string? Token);

internal sealed record EmailAddressDto(string Address, string State, DateTimeOffset? VerifiedAt, long Version);

internal static class EmailEndpoints
{
	public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/me/email", GetOwnEmailAsync)
			.WithName("getOwnEmail")
			.WithTags("Email")
			.Produces<EmailAddressDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPut("/me/email", SetOwnEmailAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("setOwnEmail")
			.WithTags("Email")
			.Produces<EmailAddressDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		members.MapDelete("/me/email", RemoveOwnEmailAsync)
			.WithName("removeOwnEmail")
			.WithTags("Email")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		members.MapPost("/me/email/confirmation-requests", ResendOwnConfirmationAsync)
			.WithName("resendOwnEmailConfirmation")
			.WithTags("Email")
			.Produces(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);

		routes.MapPost(MemberApiRouteGroup.Prefix + "/email-confirmations", ConfirmEmailAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(EmailRateLimiting.ConfirmationPolicy)
			.WithName("confirmEmail")
			.WithTags("Email")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);
		return routes;
	}

	private static EmailAddressDto ToDto(StoredMemberEmailAddress stored) =>
		new(stored.Address, stored.State, stored.VerifiedAt, stored.Version);

	private static IResult Address(HttpContext http, OperationResult<StoredMemberEmailAddress> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
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

	private static async Task<IResult> GetOwnEmailAsync(
		HttpContext http,
		IRequestContext context,
		GetEmailAddressHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		return Address(http, await handler.HandleAsync(accountId, cancellationToken));
	}

	private static async Task<IResult> SetOwnEmailAsync(
		HttpContext http,
		IRequestContext context,
		SetEmailAddressHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		long? expectedVersion = null;
		if (http.Request.Headers.ContainsKey("If-Match"))
		{
			var parsed = IfMatchHeader.Parse(http.Request);
			if (!parsed.IsSuccess)
			{
				return ProblemResults.From(parsed.Failure);
			}

			expectedVersion = parsed.Value;
		}

		var body = await ReadBodyAsync<SetEmailAddressRequest>(http, cancellationToken);
		return Address(http, await handler.HandleAsync(new SetEmailAddressCommand(accountId, accountId, body?.Address, expectedVersion), cancellationToken));
	}

	private static async Task<IResult> RemoveOwnEmailAsync(
		HttpContext http,
		IRequestContext context,
		RemoveEmailAddressHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		var result = await handler.HandleAsync(new RemoveEmailAddressCommand(accountId, version.Value), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> ResendOwnConfirmationAsync(
		HttpContext http,
		IRequestContext context,
		ResendEmailConfirmationHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(new ResendEmailConfirmationCommand(accountId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		if (result.Value.RetryAfter is { } wait)
		{
			http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
			return ProblemResults.Problem(StatusCodes.Status429TooManyRequests, EmailRateLimiting.RateLimitedCode);
		}

		return Results.Accepted();
	}

	private static async Task<IResult> ConfirmEmailAsync(
		HttpContext http,
		ConfirmEmailHandler handler,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<ConfirmEmailRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new ConfirmEmailCommand(body?.Token), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}
}
