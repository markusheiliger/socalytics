using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class GetMemberHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IMemberAccountStore members)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(GetMemberQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource("member", query.MemberId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var member = await members.GetMemberAsync(query.MemberId, cancellationToken);
        if (member is null)
        {
            return OperationFailure.NotFound();
        }

        return member;
    }
}
