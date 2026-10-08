namespace SocAlytics.Platform.Domain.Club;

// TeamId and Opponent are init-only: they never change after creation.
public sealed record Match(
    Guid Id,
    Guid TeamId,
    MatchOpponent Opponent,
    MatchDetails Details,
    DateTimeOffset CreatedAt,
    Guid CreatedByAccountId,
    long Version)
{
    public Guid TeamId { get; init; } = TeamId;

    public MatchOpponent Opponent { get; init; } = Opponent;
}
