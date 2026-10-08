namespace SocAlytics.Platform.Application.Club;

public sealed record BootstrapClubCommand(
    string? ClubDisplayName,
    string? FirstClubAdminAccountName,
    string? FirstClubAdminInitialPassword)
{
    public override string ToString() => nameof(BootstrapClubCommand);
}
