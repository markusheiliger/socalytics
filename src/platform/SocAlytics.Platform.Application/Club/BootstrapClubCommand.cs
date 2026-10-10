using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Club;

public sealed record BootstrapClubCommand(
    string? ClubDisplayName,
    string? FirstClubAdminAccountName,
    string? FirstClubAdminInitialPassword,
    ApplyBreakGlassRecoveryCommand? Recovery = null)
{
    public override string ToString() => nameof(BootstrapClubCommand);
}
