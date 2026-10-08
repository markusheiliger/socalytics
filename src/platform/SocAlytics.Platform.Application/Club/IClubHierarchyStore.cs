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
}
