using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record SignInRequest(string? AccountName, string? Password);

internal sealed record CurrentTeamRoleDto(Guid TeamId, string TeamName, Guid SeasonId, string Role);

internal sealed record CurrentMemberDto(
	Guid Id,
	string AccountName,
	string MembershipStatus,
	bool PasswordChangeRequired,
	IReadOnlyList<string> ClubRoles,
	IReadOnlyList<CurrentTeamRoleDto> TeamRoles);

internal sealed record SessionInfoDto(
	Guid SessionId,
	DateTimeOffset IdleExpiresAt,
	DateTimeOffset AbsoluteExpiresAt,
	bool PasswordChangeRequired,
	string AntiforgeryToken,
	CurrentMemberDto Member);

internal static class SessionEndpoints
{
	public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/session", SignInAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("signIn")
			.WithTags("Session")
			.Produces<SessionInfoDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

		var holder = routes.MapSessionHolderApi();
		holder.MapGet("/session", GetSessionAsync)
			.WithName("getSession")
			.WithTags("Session")
			.Produces<SessionInfoDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized);
		holder.MapDelete("/session", SignOutAsync)
			.WithName("signOut")
			.WithTags("Session")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		return routes;
	}

	private static async Task<IResult> SignInAsync(
		HttpContext http,
		SignInHandler signIn,
		GetSessionHandler sessions,
		CancellationToken cancellationToken)
	{
		SignInRequest? request;
		try
		{
			request = await http.Request.ReadFromJsonAsync<SignInRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			request = null;
		}

		request ??= new SignInRequest(null, null);
		var violations = new List<FieldViolation>();
		if (string.IsNullOrWhiteSpace(request.AccountName))
		{
			violations.Add(new FieldViolation("accountName", "required"));
		}

		if (string.IsNullOrEmpty(request.Password))
		{
			violations.Add(new FieldViolation("password", "required"));
		}

		if (violations.Count > 0)
		{
			return ProblemResults.From(OperationFailure.Validation([.. violations]));
		}

		var presented = SessionCookie.Read(http.Request);
		var result = await signIn.HandleAsync(
			new SignInCommand(request.AccountName!, request.Password!, presented is null ? null : SessionCookie.Hash(presented)),
			cancellationToken);
		if (!result.IsSuccess)
		{
			SessionCookie.Clear(http.Response);
			return ProblemResults.From(result.Failure);
		}

		var signed = result.Value;
		var details = await sessions.HandleAsync(new GetSessionQuery(SessionCookie.Hash(signed.RawToken)), cancellationToken);
		if (!details.IsSuccess)
		{
			return ProblemResults.From(details.Failure);
		}

		SessionCookie.Write(http.Response, signed.RawToken);
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(ToDto(details.Value, signed.RawToken));
	}

	private static async Task<IResult> GetSessionAsync(
		HttpContext http,
		GetSessionHandler sessions,
		CancellationToken cancellationToken)
	{
		var token = SessionCookie.Read(http.Request);
		if (token is null)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var result = await sessions.HandleAsync(new GetSessionQuery(SessionCookie.Hash(token)), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(ToDto(result.Value, token));
	}

	private static async Task<IResult> SignOutAsync(
		HttpContext http,
		IRequestContext context,
		SignOutHandler signOut,
		CancellationToken cancellationToken)
	{
		if (context.SessionId is not { } sessionId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		await signOut.HandleAsync(new SignOutCommand(sessionId), cancellationToken);
		SessionCookie.Clear(http.Response);
		return Results.NoContent();
	}

	// Team names and seasons are not available until the team hierarchy exists, so team roles are reported once they can be resolved.
	private static SessionInfoDto ToDto(SessionDetails details, string rawToken) => new(
		details.Session.SessionId,
		details.Session.IdleExpiresAt,
		details.Session.AbsoluteExpiresAt,
		details.Session.PasswordChangeRequired,
		SessionAntiforgeryFilter.ComputeToken(rawToken),
		new CurrentMemberDto(
			details.Session.AccountId,
			details.AccountName,
			details.MembershipStatus.ToWireValue(),
			details.Session.PasswordChangeRequired,
			[.. details.ClubRoles.Select(r => r.ToWireValue()).Order(StringComparer.Ordinal)],
			[]));
}
