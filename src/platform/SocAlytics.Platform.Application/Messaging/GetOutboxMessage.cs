using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Messaging;

public sealed record GetOutboxMessageQuery(Guid MessageId);

public sealed class GetOutboxMessageHandler(
    IUnitOfWork unitOfWork, IOutboxMessageQueries queries, IAccessAuthorizer authorizer, TimeProvider time)
{
    public async Task<OperationResult<OutboxMessageView>> HandleAsync(GetOutboxMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!(await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken)).IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var message = await queries.GetAsync(query.MessageId, time.GetUtcNow(), cancellationToken);
        return message is null ? OperationFailure.NotFound() : message;
    }
}
