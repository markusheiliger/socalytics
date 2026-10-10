using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record ChangeOwnPasswordRequest(string? CurrentPassword, string? NewPassword);

internal static class SelfEndpoints
{
	public static IEndpointRouteBuilder MapSelfEndpoints(this IEndpointRouteBuilder routes)
	{
		var holder = routes.MapSessionHolderApi();
		holder.MapGet("/me", GetCurrentMemberAsync)
			.WithName("getCurrentMember")
			.WithTags("Self")
			.Produces<CurrentMemberDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized);
		holder.MapPost("/me/password", ChangeOwnPasswordAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("changeOwnPassword")
			.WithTags("Self")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		return routes;
	}

	private static async Task<IResult> GetCurrentMemberAsync(
		HttpContext http,
		IRequestContext context,
		GetCurrentMemberHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(new GetCurrentMemberQuery(accountId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var profile = result.Value;
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(new CurrentMemberDto(
			profile.Id,
			profile.AccountName,
			profile.Status.ToWireValue(),
			profile.PasswordChangeRequired,
			[.. profile.ClubRoles.Select(r => r.ToWireValue()).Order(StringComparer.Ordinal)],
			[.. profile.TeamRoles.Select(t => new CurrentTeamRoleDto(t.TeamId, t.TeamName, t.SeasonId, t.Role.ToWireValue()))]));
	}

	private static async Task<IResult> ChangeOwnPasswordAsync(
		HttpContext http,
		IRequestContext context,
		ChangeOwnPasswordHandler handler,
		CancellationToken cancellationToken)
	{
		if (context.MemberAccountId is not { } accountId || context.SessionId is not { } sessionId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		ChangeOwnPasswordRequest? request;
		try
		{
			request = await http.Request.ReadFromJsonAsync<ChangeOwnPasswordRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			request = null;
		}

		request ??= new ChangeOwnPasswordRequest(null, null);
		var violations = new List<FieldViolation>();
		if (string.IsNullOrEmpty(request.CurrentPassword))
		{
			violations.Add(new FieldViolation("currentPassword", "required"));
		}

		if (string.IsNullOrEmpty(request.NewPassword))
		{
			violations.Add(new FieldViolation("newPassword", "required"));
		}

		if (violations.Count > 0)
		{
			return ProblemResults.From(OperationFailure.Validation([.. violations]));
		}

		var result = await handler.HandleAsync(
			new ChangeOwnPasswordCommand(accountId, sessionId, request.CurrentPassword!, request.NewPassword!),
			cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}
}
