using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record SubmitAgentInvocationCommand(Guid ConversationId, string? IdempotencyKey, string? AgentId, string? Question);

public static class AgentSubmissionCodes
{
    public const string IdempotencyKeyReused = "idempotency-key-reused";
    public const string AgentNotParticipant = "agent-not-participant";
    public const string AgentNotAvailable = "agent-not-available";
    public const string TooManyUnfinishedInvocations = "too-many-unfinished-invocations";
}

public sealed class SubmitAgentInvocationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAgentInvocationStore invocations,
    IAgentEnablementStore enablementStore,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IOutbox outbox,
    IContractValidator validator,
    IQueueCredentialIssuer queue,
    IAuditTrail audit,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions PolicyOptions = new(JsonSerializerDefaults.Web);

    public async Task<OperationResult<SubmittedAgentInvocation>> HandleAsync(
        SubmitAgentInvocationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"submit-invocation\n{command.ConversationId:D}\n{command.AgentId}\n{command.Question}")));

        // A concurrent request with the same key can win the insert; the second pass then replays its outcome.
        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                return await TryAsync(command, hash, ownerId, cancellationToken);
            }
            catch (UniqueViolationException) when (pass == 0)
            {
            }
        }

        throw new InvalidOperationException("The invocation could not be submitted or replayed.");
    }

    private async Task<OperationResult<SubmittedAgentInvocation>> TryAsync(
        SubmitAgentInvocationCommand command, string hash, Guid ownerId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.ConversationResourceType, command.ConversationId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        var teamId = conversation.TeamId;
        var policy = options.CurrentValue.Execution.ToPolicy();
        var key = command.IdempotencyKey;

        var invalid = Validate(command, key, policy);
        if (invalid is not null)
        {
            return await RefuseAsync(scope, conversation, command.AgentId, invalid, "validation-failed", cancellationToken);
        }

        var existing = await invocations.FindByKeyAsync(ownerId, conversation.Id, key!, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                return await RefuseAsync(
                    scope, conversation, command.AgentId, OperationFailure.Conflict(AgentSubmissionCodes.IdempotencyKeyReused),
                    AgentSubmissionCodes.IdempotencyKeyReused, cancellationToken);
            }

            var storedPolicy = AgentExecutionSupport.ResolvePolicy(existing.ExecutionPolicyJson, options.CurrentValue);
            await audit.RecordAsync(
                AgentOrchestrationAuditEvents.InvocationReplayed(existing.Id, conversation.Id, teamId, existing.AgentId), cancellationToken);
            await scope.CommitAsync(cancellationToken);
            AgentOrchestrationMetrics.SubmissionRecorded("replayed");
            return new SubmittedAgentInvocation(conversation.Id, existing.Id, storedPolicy.SuggestedPollingSeconds, true);
        }

        var agentId = command.AgentId!;
        if (!conversation.Participants.Any(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal)))
        {
            return await RefuseAsync(
                scope, conversation, agentId, OperationFailure.Conflict(AgentSubmissionCodes.AgentNotParticipant),
                AgentSubmissionCodes.AgentNotParticipant, cancellationToken);
        }

        if ((await enablementStore.FindAsync(agentId, true, cancellationToken))?.AgentVersion is null)
        {
            return await RefuseAsync(
                scope, conversation, agentId, OperationFailure.Conflict(AgentSubmissionCodes.AgentNotAvailable),
                AgentSubmissionCodes.AgentNotAvailable, cancellationToken);
        }

        if (await invocations.CountUnfinishedAsync(ownerId, cancellationToken) >= policy.MaxUnfinishedInvocationsPerOwner)
        {
            return await RefuseAsync(
                scope, conversation, agentId, OperationFailure.TooManyRequests(AgentSubmissionCodes.TooManyUnfinishedInvocations),
                AgentSubmissionCodes.TooManyUnfinishedInvocations, cancellationToken);
        }

        var now = time.GetUtcNow();
        var invocationId = Guid.CreateVersion7();
        var correlationId = Guid.CreateVersion7();
        var record = await invocations.InsertAsync(
            new NewAgentInvocation(
                invocationId, conversation.Id, teamId, conversation.MatchId, conversation.AnalysisRunId, ownerId, agentId,
                AgentRoute.UserToSpecialist, command.Question!, key!, hash, correlationId, policy.Version,
                JsonSerializer.Serialize(policy, PolicyOptions), now),
            cancellationToken);
        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocationId, "submitted", null,
                JsonSerializer.Serialize(new { agentId, route = AgentPersistedNames.ToPersistedName(AgentRoute.UserToSpecialist) }), now),
            cancellationToken);

        var payload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0.0",
            invocationId,
            route = AgentPersistedNames.ToPersistedName(record.Route),
            agentId,
            readyOccurrence = record.ReadyOccurrence,
            correlationId,
        });
        if (!validator.Validate(ContractSchemaIds.AgentWorkNotificationV1, payload).IsValid)
        {
            throw new InvalidOperationException("The work notification does not conform to work-notification/v1.");
        }

        await outbox.EnqueueAsync(
            new OutboxMessageDraft(
                Guid.CreateVersion7(), PublishOutboxBatch.AgentInvocationReadyMessageType, "1.0.0", queue.AgentWorkSubject, payload, invocationId),
            cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.InvocationAccepted(invocationId, conversation.Id, teamId, agentId), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentOrchestrationMetrics.SubmissionRecorded("accepted");
        return new SubmittedAgentInvocation(conversation.Id, invocationId, policy.SuggestedPollingSeconds, false);
    }

    private static OperationFailure? Validate(SubmitAgentInvocationCommand command, string? key, ExecutionPolicy policy)
    {
        if (string.IsNullOrEmpty(key))
        {
            return OperationFailure.Validation("idempotency-key-missing", new FieldViolation("Idempotency-Key", "required"));
        }

        if (key.Length > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            return OperationFailure.Validation(new FieldViolation("Idempotency-Key", "invalid"));
        }

        var violations = new List<FieldViolation>();
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            violations.Add(new FieldViolation("agentId", "required"));
        }

        if (string.IsNullOrWhiteSpace(command.Question))
        {
            violations.Add(new FieldViolation("question", "required"));
        }
        else if (command.Question.Length > policy.MaxQuestionCharacters)
        {
            violations.Add(new FieldViolation("question", "too-long"));
        }
        else if (command.Question.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            violations.Add(new FieldViolation("question", "invalid"));
        }

        return violations.Count == 0 ? null : OperationFailure.Validation([.. violations]);
    }

    // Refusals are audited, so the unit of work commits the audit record before the failure is returned.
    private async Task<OperationResult<SubmittedAgentInvocation>> RefuseAsync(
        IUnitOfWorkScope scope,
        AgentConversationRecord conversation,
        string? agentId,
        OperationFailure failure,
        string reason,
        CancellationToken cancellationToken)
    {
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.InvocationRefused(conversation.Id, conversation.TeamId, agentId, reason), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentOrchestrationMetrics.SubmissionRecorded("refused");
        return failure;
    }
}
