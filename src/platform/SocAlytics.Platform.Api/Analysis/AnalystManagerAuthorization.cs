using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Api.Security;

namespace SocAlytics.Platform.Api.Analysis;

internal static class AnalystManagerAuthorization
{
	public static TBuilder RequireAnalystManager<TBuilder>(this TBuilder builder)
		where TBuilder : IEndpointConventionBuilder =>
		builder.RequireAuthorization(AuthorizationPolicyNames.AnalystManager);
}
