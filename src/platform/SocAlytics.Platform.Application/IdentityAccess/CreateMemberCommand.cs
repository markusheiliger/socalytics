using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record CreateMemberCommand(string? AccountName, string? CredentialKind = null, string? Provider = null);

public sealed record ExternalIdentitySummary(string ProviderId, string DisplayName, DateTimeOffset LinkedAt, bool ProviderConfigured);

public sealed record MemberTeamAssignment(Guid TeamId, TeamRole Role);

public sealed record MemberDetails(
    Guid Id,
    string AccountName,
    MembershipStatus MembershipStatus,
    bool PasswordSet,
    bool LockedOut,
    DateTimeOffset? LockoutEndsAt,
    bool TwoFactorEnabled,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyList<MemberTeamAssignment> TeamRoles,
    DateTimeOffset CreatedAt,
    long Version,
    MemberEmailSummary? Email = null,
    IReadOnlyList<ExternalIdentitySummary>? ExternalIdentities = null);

/// <summary>The address and verification state shown to Club Admins in member views.</summary>
public sealed record MemberEmailSummary(string Address, string State);

public sealed record MemberCreation(
    MemberDetails Member,
    IssuedCredential SetPasswordCredential,
    CredentialPurpose CredentialPurpose = CredentialPurpose.SetPassword);
