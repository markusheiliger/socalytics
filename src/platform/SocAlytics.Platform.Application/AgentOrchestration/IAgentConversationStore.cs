namespace SocAlytics.Platform.Application.AgentOrchestration;

public sealed record AgentParticipantRecord(string AgentId, Guid AddedByMemberId, DateTimeOffset AddedAt);

public sealed record AgentConversationRecord(
    Guid Id,
    Guid TeamId,
    Guid MatchId,
    Guid AnalysisRunId,
    Guid OwnerMemberId,
    string State,
    string IdempotencyKey,
    string RequestHash,
    DateTimeOffset CreatedAt,
    long Version,
    IReadOnlyList<AgentParticipantRecord> Participants);

// The initial participants are added by the owner at CreatedAt.
public sealed record NewAgentConversation(
    Guid Id,
    Guid TeamId,
    Guid MatchId,
    Guid AnalysisRunId,
    Guid OwnerMemberId,
    string IdempotencyKey,
    string RequestHash,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> ParticipantAgentIds);

public sealed record AgentConversationPageKey(DateTimeOffset CreatedAt, Guid Id);

public sealed record AgentConversationPage(IReadOnlyList<AgentConversationRecord> Items, bool HasMore);

public enum AddParticipantOutcome
{
    Added,
    AlreadyParticipant,
    VersionConflict,
    NotFound,
}

public sealed record AddParticipantResult(AddParticipantOutcome Outcome, long? Version);

public interface IAgentConversationStore
{
    Task<AgentConversationRecord?> FindByOwnerAndKeyAsync(Guid ownerMemberId, string idempotencyKey, CancellationToken cancellationToken);

    // Conversation ids are assigned by the caller (Guid.CreateVersion7); the initial participants are inserted with it.
    Task InsertAsync(NewAgentConversation conversation, CancellationToken cancellationToken);

    // Locks the conversation row for the transaction and loads its participants ordered by agent id.
    Task<AgentConversationRecord?> LockByIdAsync(Guid conversationId, CancellationToken cancellationToken);

    // Adds the participant only while the conversation is at expectedVersion; a duplicate agent is reported, not inserted.
    Task<AddParticipantResult> TryAddParticipantAsync(
        Guid conversationId,
        long expectedVersion,
        string agentId,
        Guid addedByMemberId,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken);

    // Newest first by (created_at, id).
    Task<AgentConversationPage> ListByOwnerAndMatchAsync(
        Guid ownerMemberId,
        Guid matchId,
        AgentConversationPageKey? after,
        int pageSize,
        CancellationToken cancellationToken);

    // True while runId is the newest run (as the run reads order them) of the match's latest finalized recording-set version.
    Task<bool> IsCurrentRunAsync(Guid matchId, Guid runId, CancellationToken cancellationToken);
}
