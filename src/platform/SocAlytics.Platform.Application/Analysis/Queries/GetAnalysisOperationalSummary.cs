using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record WorkflowDefinitionStatusView(string State, string? Category, DateTimeOffset? Since);

public sealed record AnalysisOperationalCounts(
    IReadOnlyDictionary<string, int> RunsByState, int RunsWithBlockedNodes, int StaleAttempts, int RejectedNotifications,
    int OutboxPending, int OutboxFailed, long? OldestPendingAgeSeconds);

public sealed record AnalysisOperationalSummary(
    WorkflowDefinitionStatusView WorkflowDefinition, IReadOnlyDictionary<string, int> RunsByState, int RunsWithBlockedNodes,
    int StaleAttempts, int RejectedNotifications, int OutboxPending, int OutboxFailed, long? OldestPendingAgeSeconds,
    DateTimeOffset ObservedAt);

public sealed record NotificationReceiptView(
    Guid MessageId, string MessageType, Guid? RecordingSetVersionId, string Outcome, string? RejectionCategory, Guid? RunId,
    DateTimeOffset ReceivedAt);

public interface IAnalysisOperationalQueries
{
    Task<AnalysisOperationalCounts> GetCountsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<Messaging.OperationsPageResult<NotificationReceiptView>> ListReceiptsAsync(
        string? outcome, Messaging.OperationsPageKey? after, int pageSize, CancellationToken cancellationToken);

    Task<Guid?> GetClubIdAsync(CancellationToken cancellationToken);
}

public sealed class GetAnalysisOperationalSummaryHandler(
    IUnitOfWork unitOfWork, IAnalysisOperationalQueries queries, IAccessAuthorizer authorizer,
    IWorkflowDefinitionAvailability availability, TimeProvider time)
{
    public async Task<OperationResult<AnalysisOperationalSummary>> HandleAsync(CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!(await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken)).IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var now = time.GetUtcNow();
        var c = await queries.GetCountsAsync(now, cancellationToken);
        var status = availability.Current;
        return new AnalysisOperationalSummary(
            new WorkflowDefinitionStatusView(status.State, status.Category, status.Since),
            c.RunsByState, c.RunsWithBlockedNodes, c.StaleAttempts, c.RejectedNotifications, c.OutboxPending, c.OutboxFailed,
            c.OldestPendingAgeSeconds, now);
    }
}
