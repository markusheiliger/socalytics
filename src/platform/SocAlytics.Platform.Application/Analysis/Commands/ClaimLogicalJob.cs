using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record ClaimLogicalJobCommand(Guid LogicalJobId, string IdempotencyKey, Guid ReadinessOccurrenceId, Guid MessageId);

public sealed record ClaimGrantedResult(
    Guid AttemptId,
    Guid LogicalJobId,
    Guid ReadinessOccurrenceId,
    int AttemptNumber,
    long FencingToken,
    DateTimeOffset LeaseExpiresAt,
    int LeaseSeconds,
    int StaleTimeoutSeconds,
    int ExecutionTimeoutSeconds,
    bool Replayed);

public sealed class ClaimLogicalJobHandler(
    IUnitOfWork unitOfWork,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    ICurrentAnalystManager manager,
    TimeProvider time)
{
    public const string ClaimObsoleteCode = "claim-obsolete";
    public const string IdempotencyKeyReusedCode = "idempotency-key-reused";

    public async Task<OperationResult<ClaimGrantedResult>> HandleAsync(ClaimLogicalJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var job = await jobs.LockJobAsync(command.LogicalJobId, cancellationToken);
        if (job is null)
        {
            return OperationFailure.NotFound();
        }

        var snapshot = await jobs.LoadSnapshotAsync(job.Id, cancellationToken)
            ?? throw new InvalidOperationException("The Logical Job has no snapshot.");
        var now = time.GetUtcNow();
        var managerId = manager.ManagerId;

        var key = await attempts.FindClaimKeyAsync(managerId, command.IdempotencyKey, cancellationToken);
        if (key is not null)
        {
            if (key.LogicalJobId != job.Id || key.ReadinessOccurrenceId != command.ReadinessOccurrenceId)
            {
                return OperationFailure.Conflict(IdempotencyKeyReusedCode);
            }

            var existing = await attempts.LockAsync(key.AttemptId, cancellationToken);
            if (existing is { State: ExecutionAttemptState.Active }
                && existing.LeaseExpiresAt > now
                && job.CurrentAttemptId == existing.Id)
            {
                return ToResult(existing, snapshot, replayed: true);
            }

            return OperationFailure.Conflict(ClaimObsoleteCode);
        }

        if (job.State != LogicalJobState.Ready
            || job.CurrentReadinessOccurrenceId != command.ReadinessOccurrenceId
            || job.AttemptsUsed >= snapshot.MaxAttempts)
        {
            return OperationFailure.Conflict(ClaimObsoleteCode);
        }

        var attemptNumber = job.AttemptsUsed + 1;
        var created = await attempts.InsertAsync(
            new NewExecutionAttempt(
                Guid.CreateVersion7(), job.Id, command.ReadinessOccurrenceId, attemptNumber, managerId, now,
                now + TimeSpan.FromSeconds(snapshot.StaleTimeoutSeconds)),
            cancellationToken);
        await attempts.InsertClaimKeyAsync(
            new ClaimKeyRecord(managerId, command.IdempotencyKey, job.Id, command.ReadinessOccurrenceId, created.Id), cancellationToken);
        if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Ready, LogicalJobState.Claimed, null, attemptNumber, cancellationToken))
        {
            return OperationFailure.Conflict(ClaimObsoleteCode);
        }

        await jobs.SetCurrentOccurrenceAsync(job.Id, command.ReadinessOccurrenceId, created.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToResult(created, snapshot, replayed: false);
    }

    private static ClaimGrantedResult ToResult(ExecutionAttemptRecord a, LogicalJobSnapshotRecord s, bool replayed) => new(
        a.Id, a.LogicalJobId, a.ReadinessOccurrenceId, a.AttemptNumber, a.FencingToken, a.LeaseExpiresAt,
        s.LeaseSeconds, s.StaleTimeoutSeconds, s.ExecutionTimeoutSeconds, replayed);
}
