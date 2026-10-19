using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.IdentityAccess.Invitations;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record InvitationTeamRoleRequest(Guid? TeamId, string? Role);

internal sealed record CreateInvitationRequest(string? Address, string? AccountName, IReadOnlyList<InvitationTeamRoleRequest>? TeamRoles);

internal sealed record CheckInvitationRequest(string? Token);

internal sealed record AcceptInvitationRequest(string? Token, string? Password);

internal sealed record InvitationTeamRoleDto(Guid TeamId, string TeamName, string Role);

internal sealed record InvitationInviterDto(Guid Id, string AccountName);

internal sealed record InvitationDto(
	Guid Id,
	string State,
	string Address,
	string AccountName,
	IReadOnlyList<InvitationTeamRoleDto> TeamRoles,
	InvitationInviterDto InvitedBy,
	DateTimeOffset CreatedAt,
	DateTimeOffset? LinkIssuedAt,
	DateTimeOffset? LinkExpiresAt,
	string? DeliveryState,
	int ResendCount,
	DateTimeOffset? RevokedAt,
	DateTimeOffset? AcceptedAt,
	Guid? AcceptedMemberId,
	long Version);

internal sealed record InvitationCheckDto(string ClubName, string AccountName);

internal static class InvitationEndpoints
{
	private const string Tag = "Invitations";

	public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder routes)
	{
		var invitations = routes.MapMemberApi();
		invitations.MapPost("/invitations", CreateAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createInvitation")
			.WithTags(Tag)
			.Produces<InvitationDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

		var anonymous = routes.MapGroup(MemberApiRouteGroup.Prefix + "/invitations");
		anonymous.MapPost("/checks", CheckAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(InvitationRateLimiting.TokenPolicy)
			.WithName("checkInvitation")
			.WithTags(Tag)
			.Produces<InvitationCheckDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		anonymous.MapPost("/acceptances", AcceptAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(InvitationRateLimiting.TokenPolicy)
			.WithName("acceptInvitation")
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

	private static InvitationDto ToDto(CreatedInvitation created, TimeProvider time)
	{
		var i = created.Invitation;
		return new InvitationDto(
			i.Id,
			i.DisplayState(time.GetUtcNow()).ToWireValue(),
			i.Address,
			i.AccountName.Value,
			[.. created.TeamRoles.Select(r => new InvitationTeamRoleDto(r.TeamId, r.TeamName, r.Role.ToWireValue()))],
			new InvitationInviterDto(i.InvitedBy, created.InvitedByAccountName),
			i.CreatedAt,
			i.OpenLinkIssuedAt,
			i.OpenLinkExpiresAt,
			"queued",
			i.ResendCount,
			i.RevokedAt,
			i.AcceptedAt,
			i.AcceptedMemberId,
			i.Version);
	}

	private static async Task<IResult> CreateAsync(
		HttpContext http,
		CreateInvitationHandler handler,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<CreateInvitationRequest>(http, cancellationToken);
		var command = new CreateInvitationCommand(
			body?.Address,
			body?.AccountName,
			body?.TeamRoles?.Select(r => new InvitationTeamRoleInput(r.TeamId, r.Role)).ToList());
		var result = await handler.HandleAsync(command, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var dto = ToDto(result.Value, time);
		http.Response.Headers.ETag = IfMatchHeader.Format(dto.Version);
		return Results.Created($"{MemberApiRouteGroup.Prefix}/invitations/{dto.Id}", dto);
	}

	private static async Task<IResult> CheckAsync(
		HttpContext http,
		CheckInvitationHandler handler,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<CheckInvitationRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new CheckInvitationCommand(body?.Token), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new InvitationCheckDto(result.Value.ClubName, result.Value.AccountName))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> AcceptAsync(
		HttpContext http,
		AcceptInvitationHandler handler,
		CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<AcceptInvitationRequest>(http, cancellationToken);
		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(new AcceptInvitationCommand(body?.Token, body?.Password), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}
}
