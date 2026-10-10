using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed record CreateSeasonCommand(string? Name);

public sealed record ActivateSeasonCommand(Guid SeasonId);

public sealed record ArchiveSeasonCommand(Guid SeasonId);

public sealed record GetSeasonQuery(Guid SeasonId);

public sealed record ListSeasonsQuery(string? PageSize, string? ContinuationToken);

public sealed record SeasonPage(IReadOnlyList<Season> Items, string? ContinuationToken);
