using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record GetAgentInvocationQuery(Guid InvocationId);

public sealed class GetAgentInvocationHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IOptionsMonitor<AgentOrchestrationOptions> options)
{
    public async Task<OperationResult<AgentInvocationView>> HandleAsync(
        GetAgentInvocationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.NotFound();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.InvocationResourceType, query.InvocationId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var invocation = await invocations.FindAsync(query.InvocationId, cancellationToken);
        if (invocation is null || invocation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        AgentAcceptedAdviceRecord? advice = null;
        if (invocation.State == AgentInvocationState.Succeeded)
        {
            advice = (await invocations.ReadLineageAsync(invocation.Id, cancellationToken)).Advice;
        }

        int? retryAfter = null;
        if (invocation.State is AgentInvocationState.Queued or AgentInvocationState.Running)
        {
            retryAfter = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue).SuggestedPollingSeconds;
        }

        return new AgentInvocationView(invocation, advice, retryAfter);
    }
}
