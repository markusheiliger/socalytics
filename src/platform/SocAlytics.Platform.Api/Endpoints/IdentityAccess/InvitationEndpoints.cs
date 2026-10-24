using System.Globalization;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
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
		invitations.MapGet("/invitations", ListAsync)
			.WithName("listInvitations")
			.WithTags(Tag)
			.Produces<InvitationDto[]>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		invitations.MapPost("/invitations/{invitationId:guid}/resend", ResendAsync)
			.WithName("resendInvitation")
			.WithTags(Tag)
			.Produces<InvitationDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);
		invitations.MapPost("/invitations/{invitationId:guid}/revoke", RevokeAsync)
			.WithName("revokeInvitation")
			.WithTags(Tag)
			.Produces<InvitationDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

	private static InvitationDto ToDto(InvitationListItem item, TimeProvider time)
	{
		var i = item.Invitation;
		return new InvitationDto(
			i.Id,
			i.DisplayState(time.GetUtcNow()).ToWireValue(),
			i.Address,
			i.AccountName.Value,
			[.. item.TeamRoles.Select(r => new InvitationTeamRoleDto(r.TeamId, r.TeamName, r.Role.ToWireValue()))],
			new InvitationInviterDto(i.InvitedBy, item.InvitedByAccountName),
			i.CreatedAt,
			i.OpenLinkIssuedAt,
			i.OpenLinkExpiresAt,
			item.DeliveryState,
			i.ResendCount,
			i.RevokedAt,
			i.AcceptedAt,
			i.AcceptedMemberId,
			i.Version);
	}

	private static async Task<IResult> ListAsync(
		string? state,
		ListInvitationsHandler handler,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListInvitationsQuery(state), cancellationToken);
		return result.IsSuccess
			? Results.Ok(result.Value.Select(i => ToDto(i, time)).ToArray())
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> ResendAsync(
		Guid invitationId,
		HttpContext http,
		ResendInvitationHandler handler,
		ListInvitationsHandler reader,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		var result = await handler.HandleAsync(new ResendInvitationCommand(invitationId, version.Value), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		if (result.Value.RetryAfter is { } wait)
		{
			http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
			return ProblemResults.Problem(StatusCodes.Status429TooManyRequests, "invitation-resend-rate-limited");
		}

		return await ReadOneAsync(invitationId, http, reader, time, cancellationToken);
	}

	private static async Task<IResult> RevokeAsync(
		Guid invitationId,
		HttpContext http,
		RevokeInvitationHandler handler,
		ListInvitationsHandler reader,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		var result = await handler.HandleAsync(new RevokeInvitationCommand(invitationId, version.Value), cancellationToken);
		return result.IsSuccess
			? await ReadOneAsync(invitationId, http, reader, time, cancellationToken)
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> ReadOneAsync(
		Guid invitationId,
		HttpContext http,
		ListInvitationsHandler reader,
		TimeProvider time,
		CancellationToken cancellationToken)
	{
		var listed = await reader.HandleAsync(new ListInvitationsQuery(null), cancellationToken);
		if (!listed.IsSuccess)
		{
			return ProblemResults.From(listed.Failure);
		}

		var item = listed.Value.FirstOrDefault(i => i.Invitation.Id == invitationId);
		if (item is null)
		{
			return ProblemResults.From(OperationFailure.NotFound());
		}

		var dto = ToDto(item, time);
		http.Response.Headers.ETag = IfMatchHeader.Format(dto.Version);
		return Results.Ok(dto);
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
