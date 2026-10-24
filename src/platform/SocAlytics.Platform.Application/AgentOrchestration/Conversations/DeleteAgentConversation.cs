using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record DeleteAgentConversationCommand(Guid ConversationId, long? ExpectedVersion);

/// <summary>AlreadyDeleted is true for a repeat delete by the initiating member, which changes nothing.</summary>
public sealed record DeleteAgentConversationOutcome(bool AlreadyDeleted);

public sealed class DeleteAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<DeleteAgentConversationOutcome>> HandleAsync(
        DeleteAgentConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // A deleted conversation answers not found through the resource kind, so the tombstone is consulted first.
        var tombstone = await conversations.FindTombstoneAsync(command.ConversationId, cancellationToken);
        if (tombstone is not null)
        {
            if (tombstone.DeletedByMemberId != ownerId)
            {
                return OperationFailure.NotFound();
            }

            var repeat = await authorizer.AuthorizeTeamResourceAsync(
                new("team", tombstone.TeamId), TeamPermission.Write, cancellationToken);
            return repeat.IsGranted
                ? new DeleteAgentConversationOutcome(true)
                : OperationFailure.NotFound();
        }

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

        if (command.ExpectedVersion is not { } expectedVersion)
        {
            return OperationFailure.VersionRequired();
        }

        if (conversation.Version != expectedVersion)
        {
            return OperationFailure.VersionMismatch(conversation.Version);
        }

        var now = time.GetUtcNow();
        await attempts.FenceOpenForConversationAsync(conversation.Id, now, cancellationToken);
        var suppressed = await invocations.SuppressNonterminalAsync(conversation.Id, now, cancellationToken);

        var result = await conversations.DeleteAsync(
            new NewAgentConversationDeletion(conversation.Id, ownerId, now, suppressed), expectedVersion, cancellationToken);
        switch (result.Outcome)
        {
            case ConversationTransitionOutcome.VersionConflict:
                return OperationFailure.VersionMismatch(result.Version ?? conversation.Version);
            case ConversationTransitionOutcome.NotFound:
            case ConversationTransitionOutcome.InvalidState:
                return OperationFailure.NotFound();
        }

        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ConversationDeleted(conversation.Id, conversation.TeamId, conversation.Id, suppressed),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new DeleteAgentConversationOutcome(false);
    }
}
