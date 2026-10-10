using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Application.Club;

public interface IClubHierarchyStore
{
    /// <summary>Takes the transaction-scoped <c>club-bootstrap</c> advisory lock.</summary>
    Task LockBootstrapAsync(CancellationToken cancellationToken);

    Task<ClubEntity?> GetClubAsync(CancellationToken cancellationToken);

    /// <exception cref="Abstractions.Persistence.UniqueViolationException">The singleton club already exists.</exception>
    Task InsertClubAsync(ClubEntity club, CancellationToken cancellationToken);

    Task<VersionedWriteResult> UpdateClubDisplayNameAsync(DisplayName name, long expectedVersion, CancellationToken cancellationToken);

    Task InsertSeasonAsync(Season season, CancellationToken cancellationToken);

    Task<Season?> GetSeasonAsync(Guid id, CancellationToken cancellationToken);

    Task<SeasonPageResult> ListSeasonsAsync(SeasonPageKey? after, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a season between states only while it is in <paramref name="expectedState"/>. A state mismatch reports
    /// <see cref="VersionedWriteOutcome.ConcurrencyConflict"/>.
    /// </summary>
    /// <exception cref="Abstractions.Persistence.UniqueViolationException">Another season is already active.</exception>
    Task<VersionedWriteResult> TransitionSeasonAsync(Guid id, SeasonState expectedState, SeasonState newState, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Locks the season row <c>FOR SHARE</c> so archiving and team writes serialize; null when the season is missing.</summary>
    Task<SeasonState?> LockSeasonForShareAsync(Guid seasonId, CancellationToken cancellationToken);

    Task InsertTeamAsync(Team team, CancellationToken cancellationToken);

    Task<TeamView?> GetTeamAsync(Guid id, CancellationToken cancellationToken);

    Task<TeamPageResult> ListTeamsAsync(Guid? seasonId, TeamVisibility visibility, TeamPageKey? after, int pageSize, CancellationToken cancellationToken);

    Task InsertMatchAsync(Match match, CancellationToken cancellationToken);

    Task<Match?> GetMatchAsync(Guid id, CancellationToken cancellationToken);

    Task<MatchPageResult> ListTeamMatchesAsync(Guid teamId, MatchPageKey? after, int pageSize, CancellationToken cancellationToken);

    /// <summary>Updates only kickoff, home/away and competition; the team and opponent snapshot never change.</summary>
    Task<VersionedWriteResult> UpdateMatchDetailsAsync(Guid id, MatchDetails details, long expectedVersion, CancellationToken cancellationToken);

    Task<VersionedWriteResult> UpdateTeamNameAsync(Guid id, DisplayName name, long expectedVersion, CancellationToken cancellationToken);
}

public sealed record TeamView(Team Team, SeasonState SeasonState);

public sealed record TeamPageKey(string Name, Guid Id);

public sealed record TeamPageResult(IReadOnlyList<TeamView> Items, bool HasMore, TeamPageKey? LastKey);

public sealed record SeasonPageKey(DateTimeOffset CreatedAt, Guid Id);

public sealed record SeasonPageResult(IReadOnlyList<Season> Items, bool HasMore, SeasonPageKey? LastKey);

public sealed record MatchPageKey(DateTimeOffset KickoffAt, Guid Id);

public sealed record MatchPageResult(IReadOnlyList<Match> Items, bool HasMore, MatchPageKey? LastKey);
