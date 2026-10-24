using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record RequestAnalysisRunCommand(Guid MatchId, string? IdempotencyKey);

public sealed record RequestAnalysisRunResult(
    Guid RunId,
    bool Created,
    AnalysisRunState State,
    Guid? CurrentRunId,
    Guid RecordingSetVersionId,
    string WorkflowId,
    string WorkflowVersion);

public sealed class RequestAnalysisRunHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IRecordingSetLookup lookup,
    IWorkflowDefinitionResolver resolver,
    AnalysisRunCreator creator,
    IAnalysisRunStore runs,
    IRunSupersessionStore supersessions,
    INotificationReceiptStore receipts,
    IAnalysisRunRequestStore requests,
    IAuditTrail audit,
    TimeProvider time)
{
    public const string MessageType = "analysis-requested";
    public const string DependencyUnavailableCode = "analysis-dependency-unavailable";
    public const string NoFinalizedRecordingSetCode = "no-finalized-recording-set";
    public const string WorkflowUnresolvableCode = "workflow-unresolvable";
    public const string CategoryExtension = "category";

    public async Task<OperationResult<RequestAnalysisRunResult>> HandleAsync(
        RequestAnalysisRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        if (decision.Scope!.SeasonState == SeasonState.Archived)
        {
            return OperationFailure.Conflict("season-archived");
        }

        var key = command.IdempotencyKey;
        if (key is null || key.Length is < 1 or > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            return OperationFailure.Validation(new FieldViolation("Idempotency-Key", "invalid"));
        }

        RecordingSetLineage? lineage;
        try
        {
            lineage = await lookup.GetCurrentForMatchAsync(command.MatchId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationFailure.Unavailable(DependencyUnavailableCode);
        }

        if (lineage is null)
        {
            return OperationFailure.Conflict(NoFinalizedRecordingSetCode);
        }

        var resolution = await resolver.ResolveDefaultAsync(cancellationToken);
        if (resolution is not WorkflowResolution.ResolvedResult resolved)
        {
            var category = ((WorkflowResolution.UnavailableResult)resolution).Category;
            return category == WorkflowResolution.RegistryUnavailable
                ? OperationFailure.Unavailable(DependencyUnavailableCode)
                : OperationFailure.Conflict(WorkflowUnresolvableCode) with
                {
                    Extensions = new Dictionary<string, string> { [CategoryExtension] = category },
                };
        }

        var definition = resolved.Definition;
        var (runId, created) = await creator.CreateOrFindAsync(lineage, definition, cancellationToken);
        var run = await runs.LockRunAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("The requested analysis run cannot be read.");
        var entries = await supersessions.ListMatchRunsAsync(command.MatchId, cancellationToken);
        var current = RecordingSetVersionOrder.SelectCurrent(entries, entry => entry.OrderKey);
        Guid? currentRunId = current is not null && current.RunId != runId ? current.RunId : null;

        var actor = requestContext.MemberAccountId!.Value;
        var now = time.GetUtcNow();
        var outcome = created ? AnalysisRunRequestOutcomes.RunCreated : AnalysisRunRequestOutcomes.RunExists;
        var requestId = Guid.CreateVersion7();
        await receipts.TryInsertAsync(
            new NotificationReceiptRecord(requestId, MessageType, lineage.RecordingSetVersionId, outcome, null, runId, now), cancellationToken);
        await requests.TryInsertAsync(
            new AnalysisRunRequestRecord(key, command.MatchId, actor, requestId, outcome, null, lineage.RecordingSetVersionId, runId, currentRunId, now),
            cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = AnalysisAuditActions.RequestAnalysisRunEvent,
                Action = AnalysisAuditActions.RequestAnalysisRunAction,
                Outcome = AuditOutcome.Succeeded,
                Resource = AnalysisAuditActions.AnalysisRun(runId),
                TeamId = lineage.TeamId,
                Details = new Dictionary<string, string>
                {
                    [AnalysisAuditActions.MatchIdDetail] = command.MatchId.ToString(),
                    [AnalysisAuditActions.RecordingSetVersionIdDetail] = lineage.RecordingSetVersionId.ToString(),
                    [AnalysisAuditActions.OutcomeDetail] = outcome,
                },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new RequestAnalysisRunResult(
            runId, created, run.State, currentRunId, lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion);
    }
}
