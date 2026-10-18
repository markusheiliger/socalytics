using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Queries;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record CancelAnalysisRunCommand(Guid RunId, long ExpectedVersion, string? Reason);

public sealed class CancelAnalysisRunHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IOutbox outbox,
    IAnalysisRunQueries queries,
    RunReadinessCoordinator coordinator,
    IAuditTrail audit)
{
    public const string RunTerminalCode = "analysis-run-terminal";
    private const string AttemptCloseReason = "run-cancelled";

    public async Task<OperationResult<AnalysisRunStatusView>> HandleAsync(CancelAnalysisRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", command.RunId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var run = await runs.LockRunAsync(command.RunId, cancellationToken);
        if (run is null)
        {
            return OperationFailure.NotFound();
        }

        if (!RunCancellation.TryNormalizeReason(command.Reason, out var reason, out var violation))
        {
            return OperationFailure.Validation(new FieldViolation(violation!.Field, violation.Message));
        }

        if (run.State == AnalysisRunState.Cancelled && await IsRepeatAsync(run, command.ExpectedVersion, cancellationToken))
        {
            return await ReadAsync(command.RunId, cancellationToken);
        }

        if (run.State is not (AnalysisRunState.Requested or AnalysisRunState.Running))
        {
            return OperationFailure.Conflict(RunTerminalCode) with { State = run.State.ToString() };
        }

        if (run.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(run.Version);
        }

        var actor = requestContext.MemberAccountId!.Value;
        if (!await runs.TryCancelAsync(run.Id, run.State, actor, reason, cancellationToken))
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

        await audit.RecordAsync(AnalysisAuditEvents.RunCancelled(run.Id, run.TeamId, run.State.ToString()), cancellationToken);
        await coordinator.EnqueueRunStateAsync(run, run.State, AnalysisRunState.Cancelled, null, cancellationToken);

        var status = await ReadAsync(run.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return status;
    }

    // Each node cancelled with the run advances the run's version once more through the aggregate triggers, and nodes
    // only end Cancelled through run cancellation, so the version before the cancellation is current - nodes - 1.
    private async Task<bool> IsRepeatAsync(AnalysisRunRecord run, long expectedVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion == run.Version)
        {
            return true;
        }

        var cancelledNodes = await runs.CountCancelledNodesAsync(run.Id, cancellationToken);
        return expectedVersion == run.Version - cancelledNodes - 1;
    }

    private async Task<AnalysisRunStatusView> ReadAsync(Guid runId, CancellationToken cancellationToken) =>
        await queries.GetStatusAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("The cancelled analysis run cannot be read.");
}
