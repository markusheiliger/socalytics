using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class GetSessionHandler(
    ValidateSessionHandler validator,
    IUnitOfWork unitOfWork,
    ISessionStore sessions,
    IMemberAccountStore members)
{
    public async Task<OperationResult<SessionDetails>> HandleAsync(GetSessionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var validated = await validator.HandleAsync(new ValidateSessionQuery(query.TokenHash), cancellationToken);
        if (!validated.IsSuccess)
        {
            return validated.Failure;
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var record = await sessions.FindByTokenHashAsync(query.TokenHash, cancellationToken);
        var snapshot = await members.GetAccessSnapshotAsync(validated.Value.AccountId, cancellationToken);
        if (record is null || snapshot is null)
        {
            return OperationFailure.Unauthenticated();
        }

        return new SessionDetails(validated.Value, record.AccountName, snapshot.Status, snapshot.ClubRoles, snapshot.TeamRoles);
    }
}
