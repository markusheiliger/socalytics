using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed class AnalysisLeaseOptions
{
    public const string SectionName = "Analysis:Leases";

    public int MonitorIntervalSeconds { get; set; } = 5;

    public int BatchSize { get; set; } = 50;
}

public sealed class ExpireStaleAttemptsHandler(
    IUnitOfWork unitOfWork,
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    RunReadinessCoordinator coordinator,
    Microsoft.Extensions.Options.IOptions<AnalysisLeaseOptions> options,
    TimeProvider time)
{
    public const string LeaseExpiredReason = "lease-expired";

    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExpiredAttemptRef> due;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await attempts.ListExpiredAsync(time.GetUtcNow(), Math.Max(1, options.Value.BatchSize), cancellationToken);
        }

        var expired = 0;
        foreach (var item in due)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            await runs.LockRunAsync(item.RunId, cancellationToken);
            var job = await jobs.LockJobAsync(item.LogicalJobId, cancellationToken);
            var attempt = await attempts.LockAsync(item.AttemptId, cancellationToken);
            if (job is null
                || attempt is null
                || attempt.State != ExecutionAttemptState.Active
                || attempt.LeaseExpiresAt > time.GetUtcNow())
            {
                continue;
            }

            if (!await attempts.TryCloseAsync(attempt.Id, ExecutionAttemptState.Active, ExecutionAttemptState.Stale, LeaseExpiredReason, cancellationToken))
            {
                continue;
            }

            if (job.CurrentAttemptId == attempt.Id && job.State == LogicalJobState.Claimed)
            {
                await coordinator.RequeueOrFailAsync(job.Id, ReadinessCause.AttemptStale, cancellationToken);
            }

            await scope.CommitAsync(cancellationToken);
            expired++;
        }

        return expired;
    }
}
