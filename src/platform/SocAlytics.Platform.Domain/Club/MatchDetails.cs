using System.Diagnostics.CodeAnalysis;

namespace SocAlytics.Platform.Domain.Club;

public sealed record MatchDetails
{
    public const int CompetitionMaxLength = 100;

    private MatchDetails(DateTimeOffset kickoffAt, HomeAway homeAway, string? competition)
    {
        KickoffAt = kickoffAt;
        HomeAway = homeAway;
        Competition = competition;
    }

    public DateTimeOffset KickoffAt { get; }

    public HomeAway HomeAway { get; }

    public string? Competition { get; }

    // Competition is trimmed; null stays null. Error codes: required, too-long, invalid.
    public static bool TryCreate(
        DateTimeOffset? kickoffAt,
        HomeAway homeAway,
        string? competition,
        [NotNullWhen(true)] out MatchDetails? details,
        [NotNullWhen(false)] out string? field,
        [NotNullWhen(false)] out string? errorCode)
    {
        details = null;
        if (kickoffAt is null)
        {
            (field, errorCode) = ("kickoffAt", "required");
            return false;
        }

        if (kickoffAt.Value.Offset != TimeSpan.Zero)
        {
            (field, errorCode) = ("kickoffAt", "invalid");
            return false;
        }

        if (!Enum.IsDefined(homeAway))
        {
            (field, errorCode) = ("homeAway", "invalid");
            return false;
        }

        var trimmed = competition?.Trim();
        if (trimmed is not null)
        {
            if (trimmed.Length == 0)
            {
                (field, errorCode) = ("competition", "required");
                return false;
            }

            if (trimmed.Length > CompetitionMaxLength)
            {
                (field, errorCode) = ("competition", "too-long");
                return false;
            }
        }

        details = new MatchDetails(kickoffAt.Value, homeAway, trimmed);
        field = null;
        errorCode = null;
        return true;
    }
}
