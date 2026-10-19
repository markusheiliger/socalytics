using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record GetAgentConversationQuery(Guid ConversationId);

public sealed class GetAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext)
{
    public async Task<OperationResult<AgentConversationView>> HandleAsync(
        GetAgentConversationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.NotFound();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new("agent-conversation", query.ConversationId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        var current = await conversations.IsCurrentRunAsync(conversation.MatchId, conversation.AnalysisRunId, cancellationToken);
        return new AgentConversationView(conversation, current);
    }
}
