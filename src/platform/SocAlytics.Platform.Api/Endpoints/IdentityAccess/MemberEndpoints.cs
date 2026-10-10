using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record MemberCreateRequest(string? AccountName);

internal sealed record CredentialIssueRequest(string? Purpose);

internal sealed record TeamRoleAssignmentRequest(string? Role);

internal sealed record MemberTeamRoleDto(Guid TeamId, string Role);

internal sealed record MemberDto(
	Guid Id,
	string AccountName,
	string MembershipStatus,
	bool PasswordSet,
	bool LockedOut,
	DateTimeOffset? LockoutEndsAt,
	bool TwoFactorEnabled,
	IReadOnlyList<string> ClubRoles,
	IReadOnlyList<MemberTeamRoleDto> TeamRoles,
	DateTimeOffset CreatedAt,
	long Version);

internal sealed record IssuedCredentialDto(Guid MemberId, string Purpose, string Credential, DateTimeOffset ExpiresAt);

internal sealed record MemberCreatedDto(MemberDto Member, IssuedCredentialDto SetPasswordCredential);

internal sealed record MemberPageDto(IReadOnlyList<MemberDto> Items, string? ContinuationToken);

internal static class MemberEndpoints
{
	public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapPost("/members", CreateMemberAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createMember")
			.WithTags("Members")
			.Produces<MemberCreatedDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		members.MapGet("/members", ListMembersAsync)
			.WithName("listMembers")
			.WithTags("Members")
			.Produces<MemberPageDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapGet("/members/{memberId:guid}", GetMemberAsync)
			.WithName("getMember")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPut("/members/{memberId:guid}/club-roles/{clubRole}", AssignClubRoleAsync)
			.WithName("assignClubRole")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapDelete("/members/{memberId:guid}/club-roles/{clubRole}", RevokeClubRoleAsync)
			.WithName("revokeClubRole")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapPut("/members/{memberId:guid}/team-roles/{teamId:guid}", AssignTeamRoleAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("assignTeamRole")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		members.MapDelete("/members/{memberId:guid}/team-roles/{teamId:guid}", RevokeTeamRoleAsync)
			.WithName("revokeTeamRole")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/members/{memberId:guid}/deactivate", DeactivateMemberAsync)
			.WithName("deactivateMember")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapPost("/members/{memberId:guid}/reactivate", ReactivateMemberAsync)
			.WithName("reactivateMember")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapDelete("/members/{memberId:guid}/sessions", EndMemberSessionsAsync)
			.WithName("endMemberSessions")
			.WithTags("Members")
			.Produces(StatusCodes.Status204NoContent)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/members/{memberId:guid}/unlock", UnlockMemberAsync)
			.WithName("unlockMember")
			.WithTags("Members")
			.Produces<MemberDto>(StatusCodes.Status200OK)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/members/{memberId:guid}/credentials", IssueCredentialAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("issueCredential")
			.WithTags("Members")
			.Produces<IssuedCredentialDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		return routes;
	}

	private static async Task<IResult> IssueCredentialAsync(
		Guid memberId,
		HttpContext http,
		IssueCredentialHandler handler,
		CancellationToken cancellationToken)
	{
		CredentialIssueRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<CredentialIssueRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		var purpose = body?.Purpose switch
		{
			"set-password" => CredentialPurpose.SetPassword,
			"password-reset" => CredentialPurpose.PasswordReset,
			_ => (CredentialPurpose?)null,
		};
		if (purpose is null)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("purpose", "invalid-value")));
		}

		var result = await handler.HandleAsync(new IssueCredentialCommand(memberId, purpose.Value), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.CacheControl = "no-store";
		return Results.Created(
			$"{MemberApiRouteGroup.Prefix}/members/{memberId}",
			new IssuedCredentialDto(memberId, purpose.Value.ToWireValue(), result.Value.RawCredential, result.Value.ExpiresAt));
	}

	private static async Task<IResult> EndMemberSessionsAsync(
		Guid memberId,
		EndMemberSessionsHandler handler,
		CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new EndMemberSessionsCommand(memberId), cancellationToken);
		return result.IsSuccess ? Results.NoContent() : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> UnlockMemberAsync(
		Guid memberId,
		HttpContext http,
		UnlockMemberHandler handler,
		CancellationToken cancellationToken) =>
		RoleResult(http, await handler.HandleAsync(new UnlockMemberCommand(memberId), cancellationToken));

	private static IResult RoleResult(HttpContext http, OperationResult<MemberDetails> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
	}

	private static async Task<IResult> AssignClubRoleAsync(
		Guid memberId,
		string clubRole,
		HttpContext http,
		AssignClubRoleHandler handler,
		CancellationToken cancellationToken)
	{
		if (!ClubRoleRules.TryParse(clubRole, out var role))
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("clubRole", "invalid-value")));
		}

		return RoleResult(http, await handler.HandleAsync(new AssignClubRoleCommand(memberId, role), cancellationToken));
	}

	private static async Task<IResult> RevokeClubRoleAsync(
		Guid memberId,
		string clubRole,
		HttpContext http,
		RevokeClubRoleHandler handler,
		CancellationToken cancellationToken)
	{
		if (!ClubRoleRules.TryParse(clubRole, out var role))
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("clubRole", "invalid-value")));
		}

		return RoleResult(http, await handler.HandleAsync(new RevokeClubRoleCommand(memberId, role), cancellationToken));
	}

	private static async Task<IResult> AssignTeamRoleAsync(
		Guid memberId,
		Guid teamId,
		HttpContext http,
		AssignTeamRoleHandler handler,
		CancellationToken cancellationToken)
	{
		TeamRoleAssignmentRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<TeamRoleAssignmentRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		if (!TeamRoleRules.TryParse(body?.Role, out var role))
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("role", "invalid-value")));
		}

		return RoleResult(http, await handler.HandleAsync(new AssignTeamRoleCommand(memberId, teamId, role), cancellationToken));
	}

	private static async Task<IResult> RevokeTeamRoleAsync(
		Guid memberId,
		Guid teamId,
		HttpContext http,
		RevokeTeamRoleHandler handler,
		CancellationToken cancellationToken) =>
		RoleResult(http, await handler.HandleAsync(new RevokeTeamRoleCommand(memberId, teamId), cancellationToken));

	private static async Task<IResult> DeactivateMemberAsync(
		Guid memberId,
		HttpContext http,
		DeactivateMemberHandler handler,
		CancellationToken cancellationToken) =>
		RoleResult(http, await handler.HandleAsync(new DeactivateMemberCommand(memberId), cancellationToken));

	private static async Task<IResult> ReactivateMemberAsync(
		Guid memberId,
		HttpContext http,
		ReactivateMemberHandler handler,
		CancellationToken cancellationToken) =>
		RoleResult(http, await handler.HandleAsync(new ReactivateMemberCommand(memberId), cancellationToken));

	internal static MemberDto ToDto(MemberDetails m) => new(
		m.Id,
		m.AccountName,
		m.MembershipStatus.ToWireValue(),
		m.PasswordSet,
		m.LockedOut,
		m.LockoutEndsAt,
		m.TwoFactorEnabled,
		[.. m.ClubRoles.Select(r => r.ToWireValue()).Order(StringComparer.Ordinal)],
		[.. m.TeamRoles.Select(t => new MemberTeamRoleDto(t.TeamId, t.Role.ToWireValue()))],
		m.CreatedAt,
		m.Version);

	private static async Task<IResult> ListMembersAsync(
		string? pageSize,
		string? continuationToken,
		ListMembersHandler handler,
		CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListMembersQuery(pageSize, continuationToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new MemberPageDto([.. result.Value.Items.Select(ToDto)], result.Value.ContinuationToken))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetMemberAsync(
		Guid memberId,
		HttpContext http,
		GetMemberHandler handler,
		CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetMemberQuery(memberId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
	}

	private static async Task<IResult> CreateMemberAsync(HttpContext http, CreateMemberHandler handler, CancellationToken cancellationToken)
	{
		MemberCreateRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<MemberCreateRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new CreateMemberCommand(body?.AccountName), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var created = result.Value;
		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.ETag = IfMatchHeader.Format(created.Member.Version);
		var credential = created.SetPasswordCredential;
		return Results.Created(
			$"{MemberApiRouteGroup.Prefix}/members/{created.Member.Id}",
			new MemberCreatedDto(
				ToDto(created.Member),
				new IssuedCredentialDto(created.Member.Id, CredentialPurpose.SetPassword.ToWireValue(), credential.RawCredential, credential.ExpiresAt)));
	}
}
