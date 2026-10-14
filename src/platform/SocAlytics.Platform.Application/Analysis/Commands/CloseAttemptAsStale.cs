using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

/// <summary>
/// Closes one active attempt as stale inside the caller's unit of work: locks, closes, invalidates the fencing token
/// by leaving the attempt non-active, and consumes budget through <see cref="RunReadinessCoordinator.RequeueOrFailAsync"/>.
/// </summary>
public sealed class CloseAttemptAsStale(
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    RunReadinessCoordinator coordinator,
    TimeProvider time)
{
    public async Task<bool> HandleAsync(ExpiredAttemptRef item, string reason, bool requireExpired, CancellationToken cancellationToken)
    {
        await runs.LockRunAsync(item.RunId, cancellationToken);
        var job = await jobs.LockJobAsync(item.LogicalJobId, cancellationToken);
        var attempt = await attempts.LockAsync(item.AttemptId, cancellationToken);
        if (job is null
            || attempt is null
            || attempt.State != ExecutionAttemptState.Active
            || (requireExpired && attempt.LeaseExpiresAt > time.GetUtcNow()))
        {
            return false;
        }

        if (!await attempts.TryCloseAsync(attempt.Id, ExecutionAttemptState.Active, ExecutionAttemptState.Stale, reason, cancellationToken))
        {
            return false;
        }

        if (requireExpired)
        {
            AnalysisMetrics.LeaseExpired(null);
        }
        else
        {
            AnalysisMetrics.AttemptFenced("revoked");
        }

        AnalysisMetrics.TagActivity(item.RunId, item.LogicalJobId, attempt.Id, attempt.ManagerId);
        if (job.CurrentAttemptId == attempt.Id && job.State == LogicalJobState.Claimed)
        {
            await coordinator.RequeueOrFailAsync(job.Id, ReadinessCause.AttemptStale, cancellationToken);
        }

        return true;
    }
}
