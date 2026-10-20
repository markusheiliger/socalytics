using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record ExecutionAttemptRecord(
    Guid Id,
    Guid LogicalJobId,
    Guid ReadinessOccurrenceId,
    int AttemptNumber,
    Guid ManagerId,
    long FencingToken,
    ExecutionAttemptState State,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason);

public sealed record NewExecutionAttempt(
    Guid Id,
    Guid LogicalJobId,
    Guid ReadinessOccurrenceId,
    int AttemptNumber,
    Guid ManagerId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt);

public sealed record ClaimKeyRecord(
    Guid ManagerId,
    string IdempotencyKey,
    Guid LogicalJobId,
    Guid ReadinessOccurrenceId,
    Guid AttemptId);

public sealed record ExpiredAttemptRef(Guid AttemptId, Guid LogicalJobId, Guid RunId);

public interface IExecutionAttemptStore
{
    // Draws the fencing token from socalytics.analysis_fencing_token_seq.
    Task<ExecutionAttemptRecord> InsertAsync(NewExecutionAttempt attempt, CancellationToken cancellationToken);

    Task InsertClaimKeyAsync(ClaimKeyRecord key, CancellationToken cancellationToken);

    Task<ClaimKeyRecord?> FindClaimKeyAsync(Guid managerId, string idempotencyKey, CancellationToken cancellationToken);

    Task<ExecutionAttemptRecord?> LockAsync(Guid attemptId, CancellationToken cancellationToken);

    // Closes the attempt only while it is in expectedState.
    Task<bool> TryCloseAsync(
        Guid attemptId, ExecutionAttemptState expectedState, ExecutionAttemptState newState, string? closeReason,
        CancellationToken cancellationToken);

    // Extends the lease only while the attempt is Active.
    Task<bool> TryRenewLeaseAsync(Guid attemptId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpiredAttemptRef>> ListExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    // Lists the run's Active attempts in id order.
    Task<IReadOnlyList<ExpiredAttemptRef>> ListActiveForRunAsync(Guid runId, CancellationToken cancellationToken);

    // Closes the run's Active attempts as Cancelled with close reason run-cancelled and returns them in id order.
    Task<IReadOnlyList<ExpiredAttemptRef>> CancelActiveForRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpiredAttemptRef>> ListActiveForManagerAsync(Guid managerId, CancellationToken cancellationToken);
}
