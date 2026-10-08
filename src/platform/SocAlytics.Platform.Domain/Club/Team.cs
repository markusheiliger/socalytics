namespace SocAlytics.Platform.Domain.Club;

// SeasonId is init-only: a team never moves between seasons.
public sealed record Team(Guid Id, Guid SeasonId, DisplayName Name, DateTimeOffset CreatedAt, long Version)
{
    public Guid SeasonId { get; init; } = SeasonId;
}
