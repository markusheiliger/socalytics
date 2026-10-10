namespace SocAlytics.Platform.Domain.IdentityAccess;

public enum MembershipStatus
{
    Active,
    Deactivated,
}

public static class MembershipStatusRules
{
    public static string ToWireValue(this MembershipStatus status) => status switch
    {
        MembershipStatus.Active => "active",
        MembershipStatus.Deactivated => "deactivated",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParse(string? wireValue, out MembershipStatus status)
    {
        switch (wireValue)
        {
            case "active":
                status = MembershipStatus.Active;
                return true;
            case "deactivated":
                status = MembershipStatus.Deactivated;
                return true;
            default:
                status = default;
                return false;
        }
    }

    // active --deactivate--> deactivated
    public static bool CanDeactivate(this MembershipStatus status) => status == MembershipStatus.Active;

    // deactivated --reactivate--> active
    public static bool CanReactivate(this MembershipStatus status) => status == MembershipStatus.Deactivated;
}
