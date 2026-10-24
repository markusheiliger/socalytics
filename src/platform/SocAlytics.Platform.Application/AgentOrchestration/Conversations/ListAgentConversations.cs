using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record ListAgentConversationsQuery(Guid MatchId);

public sealed class ListAgentConversationsHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext)
{
    public const int MaxConversations = 200;

    public async Task<OperationResult<AgentConversationList>> HandleAsync(
        ListAgentConversationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.NotFound();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var page = await conversations.ListByOwnerAndMatchAsync(ownerId, query.MatchId, null, MaxConversations, "active", cancellationToken);
        var currentByRun = new Dictionary<Guid, bool>();
        var items = new List<AgentConversationView>(page.Items.Count);
        foreach (var conversation in page.Items)
        {
            if (!currentByRun.TryGetValue(conversation.AnalysisRunId, out var current))
            {
                current = await conversations.IsCurrentRunAsync(conversation.MatchId, conversation.AnalysisRunId, cancellationToken);
                currentByRun[conversation.AnalysisRunId] = current;
            }

            items.Add(new AgentConversationView(conversation, current));
        }

        return new AgentConversationList(items);
    }
}
