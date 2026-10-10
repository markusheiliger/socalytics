using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record MemberAccessSnapshot(
    MembershipStatus Status,
    bool PasswordChangeRequired,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyDictionary<Guid, TeamRole> TeamRoles);

public sealed record MemberTeamRole(Guid TeamId, string TeamName, Guid SeasonId, TeamRole Role);

public sealed record MemberProfile(
    Guid Id,
    string AccountName,
    MembershipStatus Status,
    bool PasswordChangeRequired,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyList<MemberTeamRole> TeamRoles);

public sealed record LockedAccount(MembershipStatus Status, IReadOnlySet<ClubRole> ClubRoles);

public sealed record MemberPageKey(string NormalizedAccountName, Guid Id);

public sealed record MemberPageResult(IReadOnlyList<MemberDetails> Items, bool HasMore, MemberPageKey? LastKey);

/// <summary><see cref="Changed"/> is false when the stored role already matched; <see cref="Previous"/> is the role before the write.</summary>
public sealed record TeamRoleChange(bool Changed, TeamRole? Previous);

public interface IMemberAccountStore
{
    Task<MemberPageResult> ListMembersAsync(MemberPageKey? after, int pageSize, CancellationToken cancellationToken);

    Task<MemberProfile?> GetProfileAsync(Guid accountId, CancellationToken cancellationToken);

    Task<MemberDetails?> GetMemberAsync(Guid id, CancellationToken cancellationToken);

    Task<MemberAccessSnapshot?> GetAccessSnapshotAsync(Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListAllTeamIdsAsync(CancellationToken cancellationToken);

    Task AssignClubRoleAsync(Guid accountId, ClubRole role, Guid? assignedBy, CancellationToken cancellationToken);

    Task<bool> TeamExistsAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the member's role on the team; reports no change for the same role.</summary>
    Task<TeamRoleChange> UpsertTeamRoleAsync(Guid accountId, Guid teamId, TeamRole role, Guid assignedBy, CancellationToken cancellationToken);

    /// <summary>Deletes the member's role on the team; returns the removed role or null when none existed.</summary>
    Task<TeamRole?> RemoveTeamRoleAsync(Guid accountId, Guid teamId, CancellationToken cancellationToken);

    Task<LockedAccount?> LockAccountAsync(Guid id, CancellationToken cancellationToken);

    Task LockClubAdminInvariantAsync(CancellationToken cancellationToken);

    Task<int> CountOtherActiveClubAdminsAsync(Guid exceptAccountId, CancellationToken cancellationToken);

    Task RemoveClubRoleAsync(Guid accountId, ClubRole role, CancellationToken cancellationToken);

    /// <summary>Deletes every club and team role, sets the status to deactivated, stamps <c>membership_changed_at</c>, and rotates the security stamp.</summary>
    Task DeactivateAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Sets the status to active and stamps <c>membership_changed_at</c>; restores nothing else.</summary>
    Task ReactivateAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task RotateSecurityStampAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Clears <c>lockout_end</c> and <c>access_failed_count</c>; returns whether a lockout was in effect.</summary>
    Task<bool> UnlockAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<Guid?> FindAccountIdByNameAsync(AccountName name, CancellationToken cancellationToken);
}
