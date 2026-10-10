namespace SocAlytics.Platform.Application.Club;

public sealed record CreateMatchCommand(Guid TeamId, string? OpponentName, string? KickoffAt, string? HomeAway, string? Competition);

public sealed record GetMatchQuery(Guid MatchId);

public sealed record ListTeamMatchesQuery(Guid TeamId, string? PageSize, string? ContinuationToken);

public sealed record MatchPage(IReadOnlyList<SocAlytics.Platform.Domain.Club.Match> Items, string? ContinuationToken);
