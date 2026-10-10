namespace SocAlytics.Platform.Api.Security;

internal static class MemberApiRouteGroup
{
	public const string Prefix = "/api/v1";

	/// <summary>Group for operations a session restricted by a required password change cannot reach.</summary>
	public static RouteGroupBuilder MapMemberApi(this IEndpointRouteBuilder routes) =>
		routes.MapGroup(Prefix)
			.RequireAuthorization(AuthorizationPolicyNames.ActiveMember)
			.AddEndpointFilter<SessionAntiforgeryFilter>()
			.WithMetadata(new RequiresSessionAntiforgery());

	/// <summary>Group for the self-service operations that stay available on a restricted session.</summary>
	public static RouteGroupBuilder MapSessionHolderApi(this IEndpointRouteBuilder routes) =>
		routes.MapGroup(Prefix)
			.RequireAuthorization(AuthorizationPolicyNames.SessionHolder)
			.AddEndpointFilter<SessionAntiforgeryFilter>()
			.WithMetadata(new RequiresSessionAntiforgery());
}
