using System.Diagnostics;
using System.Security.Claims;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Api.Security;

internal sealed class HttpRequestContext : IRequestContext
{
	public HttpRequestContext(IHttpContextAccessor accessor)
	{
		var httpContext = accessor.HttpContext;
		CorrelationId = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();

		if (httpContext is null)
		{
			ActorKind = AuditActorKind.System;
			return;
		}

		MemberAccountId = ParseClaim(httpContext.User, SessionClaimTypes.AccountId);
		SessionId = ParseClaim(httpContext.User, SessionClaimTypes.SessionId);
		ActorKind = MemberAccountId is not null && SessionId is not null
			? AuditActorKind.Member
			: AuditActorKind.Anonymous;
	}

	public Guid? MemberAccountId { get; }

	public Guid? SessionId { get; }

	public string CorrelationId { get; }

	public AuditActorKind ActorKind { get; }

	private static Guid? ParseClaim(ClaimsPrincipal principal, string type) =>
		Guid.TryParse(principal.FindFirst(type)?.Value, out var value) ? value : null;
}
