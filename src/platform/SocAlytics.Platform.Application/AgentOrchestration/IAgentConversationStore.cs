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
    IReadOnlyList<AgentParticipantRecord> Participants,
    DateTimeOffset? ArchivedAt = null);

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

public enum ConversationTransitionOutcome
{
    Applied,
    VersionConflict,
    InvalidState,
    NotFound,
}

public sealed record ConversationTransitionResult(ConversationTransitionOutcome Outcome, long? Version);

public sealed record NewAgentConversationDeletion(
    Guid ConversationId,
    Guid DeletedByMemberId,
    DateTimeOffset DeletedAt,
    int InvocationsSuppressed);

public sealed record AgentConversationTombstoneRecord(
    Guid ConversationId,
    Guid TeamId,
    Guid DeletedByMemberId,
    DateTimeOffset DeletedAt,
    int InvocationsSuppressed);

public sealed record AgentPurgeRecord(
    Guid ConversationId,
    string Copy,
    string State,
    string? Outcome,
    int Attempts,
    DateTimeOffset NextAttemptAt);

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

    // Newest first by (created_at, id); state is the persisted conversation state to list (active or archived).
    Task<AgentConversationPage> ListByOwnerAndMatchAsync(
        Guid ownerMemberId,
        Guid matchId,
        AgentConversationPageKey? after,
        int pageSize,
        string state,
        CancellationToken cancellationToken);

    // True while runId is the newest run (as the run reads order them) of the match's latest finalized recording-set version.
    Task<bool> IsCurrentRunAsync(Guid matchId, Guid runId, CancellationToken cancellationToken);

    // active -> archived while the conversation is at expectedVersion; a conversation in another state is InvalidState.
    Task<ConversationTransitionResult> ArchiveAsync(
        Guid conversationId, long expectedVersion, DateTimeOffset archivedAt, CancellationToken cancellationToken);

    // archived -> active while the conversation is at expectedVersion; clears ArchivedAt.
    Task<ConversationTransitionResult> UnarchiveAsync(
        Guid conversationId, long expectedVersion, CancellationToken cancellationToken);

    // Active or archived -> deleted at expectedVersion; inserts the tombstone, the purge records (primary and index,
    // pending), and the agent-conversation-purge outbox message in the caller's unit of work.
    Task<ConversationTransitionResult> DeleteAsync(
        NewAgentConversationDeletion deletion, long expectedVersion, CancellationToken cancellationToken);

    // Sets state deleted on a tombstoned conversation that is in another state; returns whether a row changed.
    Task<bool> ReassertDeletedAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<AgentConversationTombstoneRecord?> FindTombstoneAsync(Guid conversationId, CancellationToken cancellationToken);

    // Creates the missing purge records of a tombstoned conversation as pending; existing records are kept.
    Task EnsurePurgeRecordsAsync(Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken);

    // Creates the missing purge records of every tombstone (at most limit conversations per call) as pending.
    Task EnsureMissingPurgeRecordsAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken);

    // Locks due pending purge records for the transaction (FOR UPDATE SKIP LOCKED).
    Task<IReadOnlyList<AgentPurgeRecord>> ClaimDuePurgesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    // Locks unresolved purge records for the transaction (FOR UPDATE SKIP LOCKED) so that they can be retried.
    Task<IReadOnlyList<AgentPurgeRecord>> ClaimUnresolvedPurgesAsync(int limit, CancellationToken cancellationToken);

    // Pending or unresolved -> completed with the outcome (purged or not-present).
    Task CompletePurgeAsync(Guid conversationId, string copy, string outcome, DateTimeOffset now, CancellationToken cancellationToken);

    // Counts a failed attempt and schedules the next one while the record stays pending.
    Task RecordPurgeFailureAsync(
        Guid conversationId, string copy, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    // Pending -> unresolved after the attempt limit.
    Task MarkPurgeUnresolvedAsync(Guid conversationId, string copy, DateTimeOffset now, CancellationToken cancellationToken);

    // Deletes the conversation's accepted advice, lineage entries, and workload operations and blanks the invocation
    // questions (stamping purged_at), in the caller's unit of work; returns the number of rows removed or blanked.
    Task<int> PurgePrimaryAsync(Guid conversationId, DateTimeOffset purgedAt, CancellationToken cancellationToken);

    // Tombstones whose conversation is not deleted or still holds nonterminal invocations or claimed or active attempts.
    Task<IReadOnlyList<AgentConversationTombstoneRecord>> ListInconsistentTombstonesAsync(int limit, CancellationToken cancellationToken);
}
