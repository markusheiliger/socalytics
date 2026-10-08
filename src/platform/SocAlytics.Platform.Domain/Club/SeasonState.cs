namespace SocAlytics.Platform.Domain.Club;

public enum SeasonState
{
    Draft,
    Active,
    Archived,
}

public static class SeasonStateWire
{
    public static string ToWire(this SeasonState state) => state switch
    {
        SeasonState.Draft => "draft",
        SeasonState.Active => "active",
        SeasonState.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParse(string? wire, out SeasonState state)
    {
        switch (wire)
        {
            case "draft": state = SeasonState.Draft; return true;
            case "active": state = SeasonState.Active; return true;
            case "archived": state = SeasonState.Archived; return true;
            default: state = default; return false;
        }
    }
}
