using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record AddAgentConversationParticipantCommand(Guid ConversationId, long? ExpectedVersion, string? AgentId);

public sealed class AddAgentConversationParticipantHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    IOptions<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AgentConversationView>> HandleAsync(
        AddAgentConversationParticipantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // Every non-owner gets the same answer as a missing conversation.
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new("agent-conversation", command.ConversationId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        if (command.ExpectedVersion is not { } expectedVersion)
        {
            return OperationFailure.VersionRequired();
        }

        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            return OperationFailure.Validation(new FieldViolation("agentId", "required"));
        }

        var agentId = command.AgentId;
        if (!AgentCatalogIds.IsSpecialist(agentId) || !options.Value.Agents.Enabled.Contains(agentId, StringComparer.Ordinal))
        {
            return OperationFailure.Conflict("agent-not-available");
        }

        if (conversation.Participants.Any(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal)))
        {
            return OperationFailure.Conflict("participant-exists");
        }

        if (conversation.Version != expectedVersion)
        {
            return OperationFailure.VersionMismatch(conversation.Version);
        }

        var result = await conversations.TryAddParticipantAsync(
            conversation.Id, expectedVersion, agentId, ownerId, time.GetUtcNow(), cancellationToken);
        switch (result.Outcome)
        {
            case AddParticipantOutcome.AlreadyParticipant:
                return OperationFailure.Conflict("participant-exists");
            case AddParticipantOutcome.VersionConflict:
                return OperationFailure.VersionMismatch(result.Version);
            case AddParticipantOutcome.NotFound:
                return OperationFailure.NotFound();
        }

        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ParticipantAdded(conversation.Id, conversation.TeamId, agentId), cancellationToken);
        var updated = await conversations.LockByIdAsync(conversation.Id, cancellationToken)
            ?? throw new InvalidOperationException("The updated conversation could not be read.");
        var current = await conversations.IsCurrentRunAsync(updated.MatchId, updated.AnalysisRunId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new AgentConversationView(updated, current);
    }
}
