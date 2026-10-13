using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry;

public sealed record StoredCapabilityDeclaration(
    CapabilityDeclaration Declaration,
    string Repository,
    string ManifestDigest,
    string ManifestJson,
    string Source,
    DateTimeOffset RegisteredAt,
    Guid? RegisteredBy);

public interface ICapabilityDeclarationStore
{
    /// <summary>Returns false when a declaration with the same image digest already exists.</summary>
    Task<bool> TryInsertAsync(StoredCapabilityDeclaration declaration, CancellationToken cancellationToken);

    Task<StoredCapabilityDeclaration?> FindAsync(string imageDigest, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredCapabilityDeclaration>> ListAsync(CancellationToken cancellationToken);
}
