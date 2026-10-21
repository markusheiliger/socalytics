using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis;

// The in-transaction effects of ending a locked Requested or Running run as Cancelled. Callers authorize, validate,
// and own the unit of work; the effects fence queued and active work without consuming budget.
public sealed class RunCancellationEffects(
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IOutbox outbox,
    RunReadinessCoordinator coordinator)
{
    private const string AttemptCloseReason = "run-cancelled";

    public async Task ApplyAsync(
        AnalysisRunRecord run,
        CancellationCause cause,
        Guid? cancelledByAccountId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (!await runs.TryCancelAsync(run.Id, run.State, cause, cancelledByAccountId, reason, cancellationToken))
        {
            throw new InvalidOperationException("The locked run changed state concurrently.");
        }

        await runs.CancelUnfinishedNodesAsync(run.Id, cancellationToken);
        var cancelledJobIds = await jobs.CancelUnfinishedByRunAsync(run.Id, cancellationToken);
        var fenced = await attempts.CancelActiveForRunAsync(run.Id, cancellationToken);
        foreach (var jobId in cancelledJobIds)
        {
            await outbox.DiscardUnpublishedReadyWorkAsync(jobId, cancellationToken);
        }

        foreach (var attempt in fenced)
        {
            AnalysisMetrics.AttemptFenced(AttemptCloseReason);
            AnalysisMetrics.TagActivity(run.Id, attempt.LogicalJobId, attempt.AttemptId);
        }

        await coordinator.EnqueueRunStateAsync(run, run.State, AnalysisRunState.Cancelled, null, cancellationToken);
    }
}
