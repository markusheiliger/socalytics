namespace SocAlytics.Platform.Domain.Club;

public enum HomeAway
{
    Home,
    Away,
    Neutral,
}

public static class HomeAwayWire
{
    public static string ToWire(this HomeAway value) => value switch
    {
        HomeAway.Home => "home",
        HomeAway.Away => "away",
        HomeAway.Neutral => "neutral",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParse(string? wire, out HomeAway value)
    {
        switch (wire)
        {
            case "home": value = HomeAway.Home; return true;
            case "away": value = HomeAway.Away; return true;
            case "neutral": value = HomeAway.Neutral; return true;
            default: value = default; return false;
        }
    }
}
