namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class IdentityMemberAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AccountName { get; set; } = string.Empty;

    public string NormalizedAccountName { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public string SecurityStamp { get; set; } = string.Empty;

    public bool LockoutEnabled { get; set; } = true;

    public DateTimeOffset? LockoutEnd { get; set; }

    public int AccessFailedCount { get; set; }

    public bool TwoFactorEnabled { get; set; }

    public bool PasswordChangeRequired { get; set; }

    public string MembershipStatus { get; set; } = "active";

    public DateTimeOffset MembershipChangedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedByAccountId { get; set; }

    public long Version { get; set; }
}
