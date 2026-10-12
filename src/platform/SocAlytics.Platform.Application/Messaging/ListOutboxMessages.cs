using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Messaging;

public sealed record ListOutboxMessagesQuery(string? State, string? MessageType, string? PageSize, string? ContinuationToken);

public sealed class ListOutboxMessagesHandler(
    IUnitOfWork unitOfWork, IOutboxMessageQueries queries, IAccessAuthorizer authorizer, TimeProvider time)
{
    private static readonly string[] States = ["pending", "published", "failed", "discarded"];
    private static readonly string[] Types = ["matches.recordings-finalized", "analysis.ready-work", "analysis.run-state-changed"];

    public async Task<OperationResult<OperationsPage<OutboxMessageView>>> HandleAsync(ListOutboxMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!(await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken)).IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var violations = new List<FieldViolation>();
        if (query.State is not null && !States.Contains(query.State))
        {
            violations.Add(new FieldViolation("state", "invalid"));
        }

        if (query.MessageType is not null && !Types.Contains(query.MessageType))
        {
            violations.Add(new FieldViolation("messageType", "invalid"));
        }

        if (!OperationsPaging.TryParse(query.PageSize, query.ContinuationToken, out var size, out var after, violations))
        {
            return OperationFailure.Validation([.. violations]);
        }

        var page = await queries.ListAsync(query.State, query.MessageType, after, size, time.GetUtcNow(), cancellationToken);
        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        var next = page.HasMore && last is not null ? OperationsPaging.Encode(new OperationsPageKey(last.CreatedAt, last.MessageId)) : null;
        return new OperationsPage<OutboxMessageView>(page.Items, next);
    }
}
