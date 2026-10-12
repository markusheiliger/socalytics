using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed class AnalysisReconciliationOptions
{
    public const string SectionName = "Analysis:Reconciliation";

    public bool OnStartup { get; set; } = true;

    public bool OnReconnect { get; set; } = true;

    public int BatchSize { get; set; } = 200;
}

public sealed record ReconciliationResult(
    int ReadyJobsRepublished,
    int NotificationsRepublished,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed class ReconcileAnalysisPublicationHandler(
    IUnitOfWork unitOfWork,
    IAnalysisRunStore runs,
    IPublicationReconciliationStore store,
    IOptions<AnalysisReconciliationOptions> options,
    TimeProvider time)
{
    public async Task<ReconciliationResult> HandleAsync(CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var batch = Math.Max(1, options.Value.BatchSize);
        var jobs = 0;
        var notifications = 0;

        var after = Guid.Empty;
        while (true)
        {
            IReadOnlyList<Guid> runIds;
            await using (var read = await unitOfWork.BeginAsync(cancellationToken))
            {
                runIds = await store.ListRunsWithReadyJobsAsync(after, batch, cancellationToken);
            }

            if (runIds.Count == 0)
            {
                break;
            }

            foreach (var runId in runIds)
            {
                await using var scope = await unitOfWork.BeginAsync(cancellationToken);
                if (await runs.LockRunAsync(runId, cancellationToken) is null)
                {
                    continue;
                }

                jobs += await store.ResetReadyWorkAsync(runId, cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            after = runIds[^1];
        }

        after = Guid.Empty;
        while (true)
        {
            IReadOnlyList<Guid> ids;
            await using (var read = await unitOfWork.BeginAsync(cancellationToken))
            {
                ids = await store.ListNotificationsAsync(after, batch, cancellationToken);
            }

            if (ids.Count == 0)
            {
                break;
            }

            await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
            {
                notifications += await store.ResetMessagesAsync(ids, cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            after = ids[^1];
        }

        return new ReconciliationResult(jobs, notifications, startedAt, time.GetUtcNow());
    }
}
