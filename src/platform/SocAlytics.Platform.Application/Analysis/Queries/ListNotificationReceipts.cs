using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record ListNotificationReceiptsQuery(string? Outcome, string? PageSize, string? ContinuationToken);

public sealed class ListNotificationReceiptsHandler(IUnitOfWork unitOfWork, IAnalysisOperationalQueries queries, IAccessAuthorizer authorizer)
{
    private static readonly string[] Outcomes = ["run-created", "run-exists", "rejected"];

    public async Task<OperationResult<OperationsPage<NotificationReceiptView>>> HandleAsync(ListNotificationReceiptsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!(await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken)).IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var violations = new List<FieldViolation>();
        if (query.Outcome is not null && !Outcomes.Contains(query.Outcome))
        {
            violations.Add(new FieldViolation("outcome", "invalid"));
        }

        if (!OperationsPaging.TryParse(query.PageSize, query.ContinuationToken, out var size, out var after, violations))
        {
            return OperationFailure.Validation([.. violations]);
        }

        var page = await queries.ListReceiptsAsync(query.Outcome, after, size, cancellationToken);
        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        var next = page.HasMore && last is not null ? OperationsPaging.Encode(new OperationsPageKey(last.ReceivedAt, last.MessageId)) : null;
        return new OperationsPage<NotificationReceiptView>(page.Items, next);
    }
}
