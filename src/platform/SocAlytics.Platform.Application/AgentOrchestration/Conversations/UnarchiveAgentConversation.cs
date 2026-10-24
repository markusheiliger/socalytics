using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record UnarchiveAgentConversationCommand(Guid ConversationId, long? ExpectedVersion);

public sealed class UnarchiveAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit)
{
    public async Task<OperationResult<AgentConversationView>> HandleAsync(
        UnarchiveAgentConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // Viewers get 403; every other member who is not the owner gets the answer for a missing conversation.
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.ConversationResourceType, command.ConversationId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId || conversation.State == "deleted")
        {
            return OperationFailure.NotFound();
        }

        if (command.ExpectedVersion is not { } expectedVersion)
        {
            return OperationFailure.VersionRequired();
        }

        // A request the current state already satisfies changes nothing, whatever the version is.
        if (conversation.State == "active")
        {
            return new AgentConversationView(conversation, await conversations.IsCurrentRunAsync(
                conversation.MatchId, conversation.AnalysisRunId, cancellationToken));
        }

        if (conversation.Version != expectedVersion)
        {
            return OperationFailure.VersionMismatch(conversation.Version);
        }

        var result = await conversations.UnarchiveAsync(conversation.Id, expectedVersion, cancellationToken);
        switch (result.Outcome)
        {
            case ConversationTransitionOutcome.VersionConflict:
                return OperationFailure.VersionMismatch(result.Version ?? conversation.Version);
            case ConversationTransitionOutcome.NotFound:
                return OperationFailure.NotFound();
            case ConversationTransitionOutcome.InvalidState:
                throw new InvalidOperationException("The conversation changed state while it was locked.");
        }

        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ConversationUnarchived(conversation.Id, conversation.TeamId), cancellationToken);
        var updated = await conversations.LockByIdAsync(conversation.Id, cancellationToken)
            ?? throw new InvalidOperationException("The updated conversation could not be read.");
        var current = await conversations.IsCurrentRunAsync(updated.MatchId, updated.AnalysisRunId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new AgentConversationView(updated, current);
    }
}
