using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed class HttpCurrentAnalystManager(IHttpContextAccessor accessor) : ICurrentAnalystManager
{
	public Guid ManagerId =>
		Guid.TryParse(accessor.HttpContext?.User.FindFirst(AnalystManagerClaimTypes.ManagerId)?.Value, out var id)
			? id
			: throw new InvalidOperationException("No authenticated Analyst Manager.");
}
