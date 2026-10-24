using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record ArchiveAgentConversationCommand(Guid ConversationId, long? ExpectedVersion);

public sealed class ArchiveAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<AgentConversationView>> HandleAsync(
        ArchiveAgentConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // Write scope (Coach or Club Admin): Viewers are forbidden, everyone else sees a missing conversation.
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.ConversationResourceType, command.ConversationId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId
            || string.Equals(conversation.State, "deleted", StringComparison.Ordinal))
        {
            return OperationFailure.NotFound();
        }

        // A request the current state already satisfies changes nothing, whatever the version.
        if (string.Equals(conversation.State, "archived", StringComparison.Ordinal))
        {
            return await ViewAsync(conversation, cancellationToken);
        }

        if (command.ExpectedVersion is not { } expectedVersion)
        {
            return OperationFailure.VersionRequired();
        }

        if (conversation.Version != expectedVersion)
        {
            return OperationFailure.VersionMismatch(conversation.Version);
        }

        var result = await conversations.ArchiveAsync(conversation.Id, expectedVersion, time.GetUtcNow(), cancellationToken);
        switch (result.Outcome)
        {
            case ConversationTransitionOutcome.VersionConflict:
                return OperationFailure.VersionMismatch(result.Version ?? conversation.Version);
            case ConversationTransitionOutcome.NotFound:
                return OperationFailure.NotFound();
            case ConversationTransitionOutcome.InvalidState:
                return OperationFailure.NotFound();
        }

        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ConversationArchived(conversation.Id, conversation.TeamId), cancellationToken);
        var updated = await conversations.LockByIdAsync(conversation.Id, cancellationToken)
            ?? throw new InvalidOperationException("The updated conversation could not be read.");
        var view = await ViewAsync(updated, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return view;
    }

    private async Task<AgentConversationView> ViewAsync(AgentConversationRecord record, CancellationToken cancellationToken) =>
        new(record, await conversations.IsCurrentRunAsync(record.MatchId, record.AnalysisRunId, cancellationToken));
}
