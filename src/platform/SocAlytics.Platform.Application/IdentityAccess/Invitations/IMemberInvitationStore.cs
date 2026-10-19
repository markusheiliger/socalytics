using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record InvitationTeamRoleRecord(Guid TeamId, TeamRole Role);

public sealed record NewMemberInvitation(
    Guid Id,
    string Address,
    string AddressNormalized,
    string AddressHash,
    AccountName AccountName,
    Guid InvitedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InvitationTeamRoleRecord> TeamRoles);

/// <summary>An invitation with the data its handlers need besides the aggregate: the address hash and the invited team roles.</summary>
public sealed record StoredMemberInvitation(
    MemberInvitation Invitation,
    string AddressNormalized,
    string AddressHash,
    IReadOnlyList<InvitationTeamRoleRecord> TeamRoles);

/// <summary>One row of the invitation list; <paramref name="DeliveryState"/> is <c>queued</c>, <c>sent</c>, or <c>failed</c>, or <c>null</c> without a message.</summary>
public sealed record InvitationListItem(
    MemberInvitation Invitation,
    IReadOnlyList<string> TeamNames,
    string InvitedByAccountName,
    string? DeliveryState);

public sealed record StoredInvitationLink(
    Guid Id,
    Guid InvitationId,
    string State,
    byte[]? TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UsedAt);

public sealed record StoredInvitationLinkWithInvitation(StoredInvitationLink Link, StoredMemberInvitation Invitation);

public interface IMemberInvitationStore
{
    /// <summary>Whether the normalized account name belongs to a member or is reserved by a pending or expired invitation.</summary>
    Task<bool> IsAccountNameTakenAsync(string accountNameNormalized, CancellationToken cancellationToken);

    /// <summary>Whether the normalized address is the current address of a member (any state) or of a pending or expired invitation.</summary>
    Task<bool> IsAddressInUseAsync(string addressNormalized, CancellationToken cancellationToken);

    /// <summary>Inserts a pending invitation with its team roles; returns false when a unique reservation already exists.</summary>
    Task<bool> InsertAsync(NewMemberInvitation invitation, CancellationToken cancellationToken);

    /// <summary>Finds an invitation and locks its row until the unit of work ends.</summary>
    Task<StoredMemberInvitation?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Lists invitations newest first, optionally only those in one display state at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<InvitationListItem>> ListAsync(InvitationDisplayState? displayState, DateTimeOffset now, CancellationToken cancellationToken);

    Task CreatePendingLinkAsync(Guid id, Guid invitationId, DateTimeOffset createdAt, CancellationToken cancellationToken);

    /// <summary>Supersedes every open (pending or issued) link of the invitation; returns the number changed.</summary>
    Task<int> SupersedeOpenLinksAsync(Guid invitationId, CancellationToken cancellationToken);

    Task<int> CountLinksCreatedSinceAsync(Guid invitationId, DateTimeOffset since, CancellationToken cancellationToken);

    Task<StoredInvitationLink?> FindLinkAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a link by token hash with its invitation and locks both rows until the unit of work ends.</summary>
    Task<StoredInvitationLinkWithInvitation?> FindLinkByTokenHashForUpdateAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>Stores the token hash and validity of a pending or issued link; returns false when it is no longer open.</summary>
    Task<bool> IssueLinkAsync(Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks an issued link used; returns false when it is not issued.</summary>
    Task<bool> MarkLinkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken);

    Task<VersionedWriteResult> MarkRevokedAsync(Guid id, Guid revokedBy, DateTimeOffset revokedAt, long expectedVersion, CancellationToken cancellationToken);

    Task<VersionedWriteResult> MarkAcceptedAsync(Guid id, Guid memberId, DateTimeOffset acceptedAt, long expectedVersion, CancellationToken cancellationToken);
}
