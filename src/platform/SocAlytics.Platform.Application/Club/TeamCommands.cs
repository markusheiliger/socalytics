namespace SocAlytics.Platform.Application.Club;

public sealed record CreateTeamCommand(Guid SeasonId, string? Name);

public sealed record UpdateTeamCommand(Guid TeamId, string? Name, bool SeasonIdSupplied, long ExpectedVersion);

public sealed record GetTeamQuery(Guid TeamId);

public sealed record ListTeamsQuery(string? PageSize, string? ContinuationToken);

public sealed record ListSeasonTeamsQuery(Guid SeasonId, string? PageSize, string? ContinuationToken);

public sealed record TeamPage(IReadOnlyList<TeamView> Items, string? ContinuationToken);
