using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed class AnalysisLeaseOptions
{
    public const string SectionName = "Analysis:Leases";

    public int MonitorIntervalSeconds { get; set; } = 5;

    public int BatchSize { get; set; } = 50;
}

public sealed class ExpireStaleAttemptsHandler(
    IUnitOfWork unitOfWork,
    IExecutionAttemptStore attempts,
    CloseAttemptAsStale closeAsStale,
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
            if (!await closeAsStale.HandleAsync(item, LeaseExpiredReason, requireExpired: true, cancellationToken))
            {
                continue;
            }

            await scope.CommitAsync(cancellationToken);
            expired++;
        }

        return expired;
    }
}
