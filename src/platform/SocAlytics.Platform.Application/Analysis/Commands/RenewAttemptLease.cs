using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record RenewAttemptLeaseCommand(Guid AttemptId, Guid LogicalJobId, long FencingToken);

public sealed record LeaseRenewedResult(Guid AttemptId, DateTimeOffset LeaseExpiresAt);

public sealed class RenewAttemptLeaseHandler(
    IUnitOfWork unitOfWork,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    ICurrentAnalystManager manager,
    TimeProvider time)
{
    public const string AttemptObsoleteCode = "attempt-obsolete";

    public async Task<OperationResult<LeaseRenewedResult>> HandleAsync(RenewAttemptLeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var job = await jobs.LockJobAsync(command.LogicalJobId, cancellationToken);
        var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
        if (attempt is null)
        {
            return OperationFailure.NotFound();
        }

        var now = time.GetUtcNow();
        if (job is null
            || attempt.LogicalJobId != job.Id
            || attempt.State != ExecutionAttemptState.Active
            || attempt.LeaseExpiresAt <= now
            || attempt.FencingToken != command.FencingToken
            || attempt.ManagerId != manager.ManagerId
            || job.CurrentAttemptId != attempt.Id)
        {
            return OperationFailure.Conflict(AttemptObsoleteCode);
        }

        var snapshot = await jobs.LoadSnapshotAsync(job.Id, cancellationToken)
            ?? throw new InvalidOperationException("The Logical Job has no snapshot.");
        var expires = now + TimeSpan.FromSeconds(snapshot.StaleTimeoutSeconds);
        if (!await attempts.TryRenewLeaseAsync(attempt.Id, expires, cancellationToken))
        {
            return OperationFailure.Conflict(AttemptObsoleteCode);
        }

        await scope.CommitAsync(cancellationToken);
        return new LeaseRenewedResult(attempt.Id, expires);
    }
}
