using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

/// <summary><paramref name="State"/> is the optional display state filter: <c>pending</c>, <c>expired</c>, <c>accepted</c>, or <c>revoked</c>.</summary>
public sealed record ListInvitationsQuery(string? State);

public sealed class ListInvitationsHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberInvitationStore invitations,
    TimeProvider time)
{
    public async Task<OperationResult<IReadOnlyList<InvitationListItem>>> HandleAsync(ListInvitationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("invitation", null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        InvitationDisplayState? filter = null;
        if (query.State is not null)
        {
            if (!InvitationStateRules.TryParse(query.State, out InvitationDisplayState parsed))
            {
                return OperationFailure.Validation(new FieldViolation("state", "invalid-value"));
            }

            filter = parsed;
        }

        var items = await invitations.ListAsync(filter, time.GetUtcNow(), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return OperationResult<IReadOnlyList<InvitationListItem>>.Success(items);
    }
}
