using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Routing;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration.Tools;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Api.AgentOrchestration;

/// <summary>
/// Records <c>agent.tool.call.refused</c> with reason <c>caller-not-workload</c> when authentication or the
/// <c>AgentWorkload</c> policy fails for an agent tool endpoint, then delegates to the default handler.
/// </summary>
internal sealed class AgentToolAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
	private const string GroupPrefix = "/api/v1/agent-tools";

	private readonly AuthorizationMiddlewareResultHandler _default = new();

	public async Task HandleAsync(
		RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
	{
		if (!authorizeResult.Succeeded && context.GetEndpoint() is { } endpoint && IsToolEndpoint(endpoint))
		{
			var attemptId = context.GetRouteValue("attemptId") is string raw && Guid.TryParse(raw, out var parsed)
				? parsed
				: Guid.Empty;
			var operationId = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
			var toolId = AgentToolAllowlist.Entries.FirstOrDefault(e => e.OperationId == operationId)?.ToolId;

			var audit = context.RequestServices.GetRequiredService<IAuditTrail>();
			await audit.RecordIndependentAsync(
				AgentToolAuditEvents.CallerNotWorkloadRefused(attemptId, toolId), context.RequestAborted);
		}

		await _default.HandleAsync(next, context, policy, authorizeResult);
	}

	private static bool IsToolEndpoint(Endpoint endpoint) =>
		endpoint is RouteEndpoint route
		&& route.RoutePattern.RawText?.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase) == true;
}
