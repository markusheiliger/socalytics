using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class GetCurrentMemberHandler(IUnitOfWork unitOfWork, IMemberAccountStore members)
{
    public async Task<OperationResult<MemberProfile>> HandleAsync(GetCurrentMemberQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var profile = await members.GetProfileAsync(query.AccountId, cancellationToken);
        if (profile is null)
        {
            return OperationFailure.Unauthenticated();
        }

        return profile;
    }
}
