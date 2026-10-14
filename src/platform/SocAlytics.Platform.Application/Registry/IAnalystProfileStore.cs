using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry;

public sealed record StoredAnalystProfile(AnalystProfile Profile, long Version);

public interface IAnalystProfileStore
{
    /// <summary>Returns false when the profile ID and version or the content digest already exists.</summary>
    Task<bool> TryInsertAsync(AnalystProfile profile, CancellationToken cancellationToken);

    Task<StoredAnalystProfile?> FindAsync(string profileId, int version, CancellationToken cancellationToken);

    Task<StoredAnalystProfile?> FindByDigestAsync(string digest, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredAnalystProfile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Persists the state fields of a transitioned profile if the stored version still equals <paramref name="expectedVersion"/>.</summary>
    Task<VersionedWriteResult> UpdateStateAsync(AnalystProfile profile, long expectedVersion, CancellationToken cancellationToken);
}
