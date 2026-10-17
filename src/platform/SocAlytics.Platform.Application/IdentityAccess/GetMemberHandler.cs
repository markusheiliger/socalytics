using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class GetMemberHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IMemberAccountStore members,
    SocAlytics.Platform.Application.IdentityAccess.Email.IMemberEmailAddressStore addresses,
    SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn.IExternalIdentityStore links,
    Microsoft.Extensions.Options.IOptions<IdentityAccessOptions> options)
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

        var email = (await addresses.ListSummariesAsync([member.Id], cancellationToken)).GetValueOrDefault(member.Id);
        var providers = options.Value.ExternalProviders;
        var identities = (await links.ListByAccountAsync(member.Id, cancellationToken))
            .Select(l => providers.TryGetValue(l.ProviderId, out var configured)
                ? new ExternalIdentitySummary(l.ProviderId, configured.DisplayName, l.LinkedAt, true)
                : new ExternalIdentitySummary(l.ProviderId, l.ProviderId, l.LinkedAt, false))
            .ToList();
        return member with { Email = email, ExternalIdentities = identities };
    }
}
