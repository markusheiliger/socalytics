namespace SocAlytics.Platform.Domain.IdentityAccess;

public enum ClubRole
{
    ClubAdmin,
    Registrar,
}

public static class ClubRoleRules
{
    public static string ToWireValue(this ClubRole role) => role switch
    {
        ClubRole.ClubAdmin => "club-admin",
        ClubRole.Registrar => "registrar",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool TryParse(string? wireValue, out ClubRole role)
    {
        switch (wireValue)
        {
            case "club-admin":
                role = ClubRole.ClubAdmin;
                return true;
            case "registrar":
                role = ClubRole.Registrar;
                return true;
            default:
                role = default;
                return false;
        }
    }

    // club-admin includes all registrar authority (FR-032).
    public static bool Includes(this ClubRole held, ClubRole required) =>
        held == required || held == ClubRole.ClubAdmin;
}
