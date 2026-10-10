namespace SocAlytics.Platform.Domain.IdentityAccess;

public enum TeamRole
{
    Coach,
    Viewer,
}

public static class TeamRoleRules
{
    public static string ToWireValue(this TeamRole role) => role switch
    {
        TeamRole.Coach => "coach",
        TeamRole.Viewer => "viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool TryParse(string? wireValue, out TeamRole role)
    {
        switch (wireValue)
        {
            case "coach":
                role = TeamRole.Coach;
                return true;
            case "viewer":
                role = TeamRole.Viewer;
                return true;
            default:
                role = default;
                return false;
        }
    }
}
