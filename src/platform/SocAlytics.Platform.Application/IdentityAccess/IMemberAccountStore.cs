using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record MemberAccessSnapshot(
    MembershipStatus Status,
    bool PasswordChangeRequired,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyDictionary<Guid, TeamRole> TeamRoles);

public interface IMemberAccountStore
{
    Task<MemberAccessSnapshot?> GetAccessSnapshotAsync(Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListAllTeamIdsAsync(CancellationToken cancellationToken);
}
