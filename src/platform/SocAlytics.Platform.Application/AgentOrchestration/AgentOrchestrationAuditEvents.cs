using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.AgentOrchestration;

/// <summary>Agent orchestration audit events. Details carry identifiers and reason codes only, never question, advice, or note text.</summary>
public static class AgentOrchestrationAuditEvents
{
    public const string ConversationCreatedEvent = "agent.conversation.created";
    public const string ParticipantAddedEvent = "agent.participant.added";
    public const string InvocationSubmittedEvent = "agent.invocation.submitted";
    public const string AttemptClaimedEvent = "agent.attempt.claimed";
    public const string AttemptCompletedEvent = "agent.attempt.completed";
    public const string AttemptFailedEvent = "agent.attempt.failed";
    public const string AttemptExpiredEvent = "agent.attempt.expired";
    public const string InvocationExhaustedEvent = "agent.invocation.exhausted";
    public const string AttemptScopeLostEvent = "agent.attempt.scope-lost";
    public const string ConversationArchivedEvent = "agent.conversation.archived";
    public const string ConversationUnarchivedEvent = "agent.conversation.unarchived";
    public const string ConversationDeletedEvent = "agent.conversation.deleted";
    public const string ConversationPurgeCompletedEvent = "agent.conversation.purge-completed";
    public const string ConversationPurgeUnresolvedEvent = "agent.conversation.purge-unresolved";

    public const string CredentialIssuedEvent = "agent-workload.credential-issued";
    public const string CredentialRefusedEvent = "agent-workload.credential-refused";

    public const string WorkloadResourceType = "agent-workload";
    public const string ConversationResourceType = "agent-conversation";
    public const string InvocationResourceType = "agent-invocation";

    public const string ConversationIdDetail = "conversationId";
    public const string InvocationIdDetail = "invocationId";
    public const string AttemptIdDetail = "attemptId";
    public const string AttemptNumberDetail = "attemptNumber";
    public const string AgentIdDetail = "agentId";
    public const string WorkloadClientIdDetail = "workloadClientId";
    public const string DispositionDetail = "disposition";
    public const string ReasonCodeDetail = "reasonCode";
    public const string TeamIdDetail = "teamId";
    public const string InvocationCountDetail = "invocationCount";
    public const string TombstoneIdDetail = "tombstoneId";
    public const string CopyDetail = "copy";

    public static AuditEvent ConversationCreated(Guid conversationId, Guid teamId) =>
        Build(ConversationCreatedEvent, "create-conversation", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [(ConversationIdDetail, conversationId.ToString())]);

    public static AuditEvent ParticipantAdded(Guid conversationId, Guid teamId, string agentId) =>
        Build(ParticipantAddedEvent, "add-participant", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [(ConversationIdDetail, conversationId.ToString()), (AgentIdDetail, agentId)]);

    public static AuditEvent InvocationAccepted(Guid invocationId, Guid conversationId, Guid teamId, string agentId) =>
        Submitted(AuditOutcome.Succeeded, invocationId, conversationId, teamId, agentId, null);

    public static AuditEvent InvocationReplayed(Guid invocationId, Guid conversationId, Guid teamId, string agentId) =>
        Submitted(AuditOutcome.Unchanged, invocationId, conversationId, teamId, agentId, null);

    public static AuditEvent InvocationRefused(Guid conversationId, Guid teamId, string? agentId, string reason) =>
        Build(InvocationSubmittedEvent, "submit-invocation", AuditOutcome.Refused, ConversationResourceType, conversationId, teamId, reason,
            Pairs((ConversationIdDetail, conversationId.ToString()), (AgentIdDetail, agentId), (ReasonCodeDetail, reason)));

    public static AuditEvent AttemptClaimed(Guid attemptId, Guid invocationId, Guid teamId, int attemptNumber, string agentId, string clientId) =>
        Workload(AttemptClaimedEvent, "claim-attempt", AuditOutcome.Succeeded, attemptId, invocationId, teamId, attemptNumber, agentId, clientId, null, null);

    public static AuditEvent ClaimRefused(Guid invocationId, Guid? teamId, string? agentId, string clientId, string disposition) =>
        Build(AttemptClaimedEvent, "claim-attempt", AuditOutcome.Refused, InvocationResourceType, invocationId, teamId, disposition,
            Pairs((InvocationIdDetail, invocationId.ToString()), (AgentIdDetail, agentId), (WorkloadClientIdDetail, clientId), (DispositionDetail, disposition)),
            WorkloadActor);

    public static AuditEvent AttemptCompleted(Guid attemptId, Guid invocationId, Guid teamId, int attemptNumber, string agentId, string clientId) =>
        Workload(AttemptCompletedEvent, "complete-attempt", AuditOutcome.Succeeded, attemptId, invocationId, teamId, attemptNumber, agentId, clientId, null, null);

    public static AuditEvent AttemptFailed(Guid attemptId, Guid invocationId, Guid teamId, int attemptNumber, string agentId, string clientId, string reason) =>
        Workload(AttemptFailedEvent, "fail-attempt", AuditOutcome.Failed, attemptId, invocationId, teamId, attemptNumber, agentId, clientId, reason, null);

    public static AuditEvent AttemptExpired(Guid attemptId, Guid invocationId, Guid teamId, int attemptNumber, string agentId, string reason) =>
        Workload(AttemptExpiredEvent, "expire-attempt", AuditOutcome.Failed, attemptId, invocationId, teamId, attemptNumber, agentId, null, reason, null, SystemActor);

    public static AuditEvent InvocationExhausted(Guid invocationId, Guid teamId, string agentId, int attemptNumber, string reason) =>
        Build(InvocationExhaustedEvent, "exhaust-invocation", AuditOutcome.Failed, InvocationResourceType, invocationId, teamId, reason,
            [(InvocationIdDetail, invocationId.ToString()), (AgentIdDetail, agentId), (AttemptNumberDetail, attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), (ReasonCodeDetail, reason)],
            SystemActor);

    public static AuditEvent AttemptScopeLost(Guid attemptId, Guid invocationId, Guid teamId, int attemptNumber, string agentId, string disposition) =>
        Workload(AttemptScopeLostEvent, "fence-attempt", AuditOutcome.Refused, attemptId, invocationId, teamId, attemptNumber, agentId, null, disposition, disposition, SystemActor);

    public static AuditEvent ClaimScopeLost(Guid invocationId, Guid teamId, string agentId, string clientId) =>
        Build(AttemptScopeLostEvent, "refuse-claim", AuditOutcome.Refused, InvocationResourceType, invocationId, teamId, "scope-lost",
            Pairs((InvocationIdDetail, invocationId.ToString()), (AgentIdDetail, agentId), (WorkloadClientIdDetail, clientId), (DispositionDetail, "scope-lost")),
            WorkloadActor);

    public static AuditEvent ConversationArchived(Guid conversationId, Guid teamId) =>
        Build(ConversationArchivedEvent, "archive-conversation", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [(ConversationIdDetail, conversationId.ToString()), (TeamIdDetail, teamId.ToString())]);

    public static AuditEvent ConversationUnarchived(Guid conversationId, Guid teamId) =>
        Build(ConversationUnarchivedEvent, "unarchive-conversation", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [(ConversationIdDetail, conversationId.ToString()), (TeamIdDetail, teamId.ToString())]);

    public static AuditEvent ConversationDeleted(Guid conversationId, Guid teamId, Guid tombstoneId, int invocationCount) =>
        Build(ConversationDeletedEvent, "delete-conversation", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [
                (ConversationIdDetail, conversationId.ToString()),
                (TeamIdDetail, teamId.ToString()),
                (TombstoneIdDetail, tombstoneId.ToString()),
                (InvocationCountDetail, invocationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]);

    public static AuditEvent ConversationPurgeCompleted(Guid conversationId, Guid teamId, Guid tombstoneId, string copy) =>
        Build(ConversationPurgeCompletedEvent, "purge-conversation", AuditOutcome.Succeeded, ConversationResourceType, conversationId, teamId, null,
            [
                (ConversationIdDetail, conversationId.ToString()),
                (TeamIdDetail, teamId.ToString()),
                (TombstoneIdDetail, tombstoneId.ToString()),
                (CopyDetail, copy),
            ],
            SystemActor);

    public static AuditEvent ConversationPurgeUnresolved(Guid conversationId, Guid teamId, Guid tombstoneId, string copy, string reason) =>
        Build(ConversationPurgeUnresolvedEvent, "purge-conversation", AuditOutcome.Failed, ConversationResourceType, conversationId, teamId, reason,
            [
                (ConversationIdDetail, conversationId.ToString()),
                (TeamIdDetail, teamId.ToString()),
                (TombstoneIdDetail, tombstoneId.ToString()),
                (CopyDetail, copy),
                (ReasonCodeDetail, reason),
            ],
            SystemActor);

    public static AuditEvent WorkloadCredentialIssued(string clientId) =>
        Build(CredentialIssuedEvent, "issue-credential", AuditOutcome.Succeeded, WorkloadResourceType, clientId, null, null,
            [(WorkloadClientIdDetail, clientId)], WorkloadActor);

    // The client id is caller-supplied for refusals, so it is bounded and only recorded as the resource.
    public static AuditEvent WorkloadCredentialRefused(string? clientId, string reason) =>
        Build(CredentialRefusedEvent, "issue-credential", AuditOutcome.Refused, WorkloadResourceType,
            clientId is { Length: > 128 } ? clientId[..128] : clientId, null, reason,
            Pairs((ReasonCodeDetail, reason)), WorkloadActor);

    private static readonly AuditActorOverride WorkloadActor = new(AuditActorKind.AgentWorkload, null);
    private static readonly AuditActorOverride SystemActor = new(AuditActorKind.System, null);

    private static AuditEvent Submitted(AuditOutcome outcome, Guid invocationId, Guid conversationId, Guid teamId, string agentId, string? reason) =>
        Build(InvocationSubmittedEvent, "submit-invocation", outcome, InvocationResourceType, invocationId, teamId, reason,
            [(ConversationIdDetail, conversationId.ToString()), (InvocationIdDetail, invocationId.ToString()), (AgentIdDetail, agentId)]);

    private static AuditEvent Workload(
        string eventType,
        string action,
        AuditOutcome outcome,
        Guid attemptId,
        Guid invocationId,
        Guid teamId,
        int attemptNumber,
        string agentId,
        string? clientId,
        string? reason,
        string? disposition,
        AuditActorOverride? actor = null) =>
        Build(eventType, action, outcome, InvocationResourceType, invocationId, teamId, reason,
            Pairs(
                (InvocationIdDetail, invocationId.ToString()),
                (AttemptIdDetail, attemptId.ToString()),
                (AttemptNumberDetail, attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (AgentIdDetail, agentId),
                (WorkloadClientIdDetail, clientId),
                (DispositionDetail, disposition),
                (ReasonCodeDetail, reason)),
            actor ?? WorkloadActor);

    private static AuditEvent Build(
        string eventType,
        string action,
        AuditOutcome outcome,
        string resourceType,
        Guid resourceId,
        Guid? teamId,
        string? reason,
        IEnumerable<(string Key, string Value)> details,
        AuditActorOverride? actor = null) =>
        Build(eventType, action, outcome, resourceType, resourceId.ToString(), teamId, reason, details, actor);

    private static AuditEvent Build(
        string eventType,
        string action,
        AuditOutcome outcome,
        string resourceType,
        string? resourceId,
        Guid? teamId,
        string? reason,
        IEnumerable<(string Key, string Value)> details,
        AuditActorOverride? actor = null) =>
        new()
        {
            EventType = eventType,
            Action = action,
            Outcome = outcome,
            Resource = new AuditResource(resourceType, resourceId),
            TeamId = teamId,
            ReasonCode = reason,
            Details = details.ToDictionary(d => d.Key, d => d.Value),
            ActorOverride = actor,
        };

    // Absent values are left out so details never carry empty identifiers.
    private static IEnumerable<(string Key, string Value)> Pairs(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => (p.Key, p.Value!));
}
