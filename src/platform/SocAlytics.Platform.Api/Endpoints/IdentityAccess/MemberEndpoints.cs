using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record MemberCreateRequest(string? AccountName);

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
		return routes;
	}

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
