using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration;

public sealed record AgentInvocationRecord(
    Guid Id,
    Guid ConversationId,
    Guid TeamId,
    Guid MatchId,
    Guid AnalysisRunId,
    Guid OwnerMemberId,
    string AgentId,
    AgentRoute Route,
    string Question,
    string IdempotencyKey,
    string RequestHash,
    Guid CorrelationId,
    string PolicyVersion,
    string ExecutionPolicyJson,
    AgentInvocationState State,
    int AttemptsUsed,
    Guid? CurrentAttemptId,
    int ReadyOccurrence,
    DateTimeOffset? LastNotifiedAt,
    AgentFailureReason? FailureReason,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? EndedAt,
    long ProgressVersion,
    long Version);

// A new invocation starts queued with its first ready occurrence and no attempt.
public sealed record NewAgentInvocation(
    Guid Id,
    Guid ConversationId,
    Guid TeamId,
    Guid MatchId,
    Guid AnalysisRunId,
    Guid OwnerMemberId,
    string AgentId,
    AgentRoute Route,
    string Question,
    string IdempotencyKey,
    string RequestHash,
    Guid CorrelationId,
    string PolicyVersion,
    string ExecutionPolicyJson,
    DateTimeOffset SubmittedAt);

public sealed record AgentInvocationRef(Guid InvocationId, int ReadyOccurrence);

public sealed record NewAgentLineageEntry(
    Guid InvocationId,
    string Kind,
    Guid? AttemptId,
    string PayloadJson,
    DateTimeOffset RecordedAt);

public sealed record AgentLineageEntryRecord(
    int Sequence,
    string Kind,
    Guid? AttemptId,
    string PayloadJson,
    DateTimeOffset RecordedAt);

public sealed record NewAgentAcceptedAdvice(
    Guid InvocationId,
    Guid AttemptId,
    string Advice,
    string ExecutionReferencesJson,
    DateTimeOffset AcceptedAt);

public sealed record AgentAcceptedAdviceRecord(
    Guid InvocationId,
    Guid AttemptId,
    string Advice,
    string ExecutionReferencesJson,
    DateTimeOffset AcceptedAt);

public sealed record AgentInvocationLineage(
    IReadOnlyList<AgentLineageEntryRecord> Entries,
    AgentAcceptedAdviceRecord? Advice);

// Turns are ordered by (SubmittedAt, InvocationId); the last turn read is the cursor of the next page.
public sealed record AgentTurnCursor(DateTimeOffset SubmittedAt, Guid InvocationId);

public sealed record AgentTurnRecord(
    Guid InvocationId,
    string AgentId,
    string Question,
    AgentInvocationState State,
    AgentFailureReason? FailureReason,
    DateTimeOffset SubmittedAt,
    AgentAcceptedAdviceRecord? Advice);

public interface IAgentInvocationStore
{
    Task<AgentInvocationRecord?> FindByKeyAsync(
        Guid ownerMemberId, Guid conversationId, string idempotencyKey, CancellationToken cancellationToken);

    Task<AgentInvocationRecord?> FindAsync(Guid invocationId, CancellationToken cancellationToken);

    Task<AgentInvocationRecord> InsertAsync(NewAgentInvocation invocation, CancellationToken cancellationToken);

    // Row lock for the rest of the unit of work.
    Task<AgentInvocationRecord?> LockAsync(Guid invocationId, CancellationToken cancellationToken);

    // Counts the owner's invocations where state IN ('queued','running').
    Task<int> CountUnfinishedAsync(Guid ownerMemberId, CancellationToken cancellationToken);

    // Counts all invocations in state queued, for the queued gauge.
    Task<long> CountQueuedAsync(CancellationToken cancellationToken);

    // Applies only while the invocation is in expectedState at expectedVersion; increments progress_version.
    // failureReason and endedAt are stored as given (null for a non-terminal state).
    Task<VersionedWriteResult> TransitionAsync(
        Guid invocationId, AgentInvocationState expectedState, long expectedVersion, AgentInvocationState newState,
        AgentFailureReason? failureReason, DateTimeOffset? endedAt, CancellationToken cancellationToken);

    // Sets current_attempt_id (null when queued or ended) and attempts_used; increments progress_version.
    Task SetCurrentAttemptAsync(Guid invocationId, Guid? attemptId, int attemptsUsed, CancellationToken cancellationToken);

    Task SetReadyOccurrenceAsync(Guid invocationId, int readyOccurrence, CancellationToken cancellationToken);

    Task SetLastNotifiedAsync(Guid invocationId, DateTimeOffset notifiedAt, CancellationToken cancellationToken);

    // Increments progress_version for a change the owner sees that is not a state or attempt change (checkpoints).
    Task TouchProgressAsync(Guid invocationId, CancellationToken cancellationToken);

    // Queued invocations never notified or last notified before the bound, oldest first.
    Task<IReadOnlyList<AgentInvocationRef>> ListQueuedNotifiedBeforeAsync(
        DateTimeOffset notifiedBefore, int batchSize, CancellationToken cancellationToken);

    // Callers hold the invocation lock; returns the gap-free sequence assigned.
    Task<int> AppendLineageAsync(NewAgentLineageEntry entry, CancellationToken cancellationToken);

    Task InsertAdviceAsync(NewAgentAcceptedAdvice advice, CancellationToken cancellationToken);

    // Submission order, strictly after the cursor when given.
    Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAsync(
        Guid conversationId, AgentTurnCursor? after, int limit, CancellationToken cancellationToken);

    // Succeeded turns submitted before the given instant, newest first.
    Task<IReadOnlyList<AgentTurnRecord>> ListSucceededTurnsBeforeAsync(
        Guid conversationId, DateTimeOffset submittedBefore, int limit, CancellationToken cancellationToken);

    Task<AgentInvocationLineage> ReadLineageAsync(Guid invocationId, CancellationToken cancellationToken);
}

public sealed record AgentAttemptRecord(
    Guid Id,
    Guid InvocationId,
    int AttemptNumber,
    string WorkloadClientId,
    long FencingToken,
    AgentAttemptState State,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset DeadlineAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    long Version);

public sealed record NewAgentAttempt(
    Guid Id,
    Guid InvocationId,
    int AttemptNumber,
    string WorkloadClientId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset DeadlineAt);

// Reason is lease-expired or deadline-reached (the deadline wins when both passed).
public sealed record AgentExpiredAttemptRef(Guid AttemptId, Guid InvocationId, string Reason);

public sealed record AgentActiveAttemptRef(Guid AttemptId, Guid InvocationId, Guid OwnerMemberId, Guid TeamId);

public interface IAgentAttemptStore
{
    // Inserts in state claimed with the next fencing token of the invocation (strictly increasing, starting at 1).
    Task<AgentAttemptRecord> InsertAsync(NewAgentAttempt attempt, CancellationToken cancellationToken);

    Task<AgentAttemptRecord?> LockAsync(Guid attemptId, CancellationToken cancellationToken);

    // Unlocked read, for callers that must take the invocation lock before the attempt lock.
    Task<AgentAttemptRecord?> FindAsync(Guid attemptId, CancellationToken cancellationToken);

    // Applies only while the attempt is in expectedState; sets ended_at and end_reason as given.
    Task<bool> TryTransitionAsync(
        Guid attemptId, AgentAttemptState expectedState, AgentAttemptState newState, string? endReason,
        DateTimeOffset? endedAt, CancellationToken cancellationToken);

    // Extends the lease only while the attempt is active.
    Task<bool> TryRenewLeaseAsync(Guid attemptId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken);

    // Active attempts whose lease or deadline is at or before now, oldest first.
    Task<IReadOnlyList<AgentExpiredAttemptRef>> ListExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentActiveAttemptRef>> ListActiveForScopeCheckAsync(CancellationToken cancellationToken);
}
