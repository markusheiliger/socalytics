using SocAlytics.Platform.Application.Abstractions.Persistence;
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
}

public sealed record SeasonPageKey(DateTimeOffset CreatedAt, Guid Id);

public sealed record SeasonPageResult(IReadOnlyList<Season> Items, bool HasMore, SeasonPageKey? LastKey);
