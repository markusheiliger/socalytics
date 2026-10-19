using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.StatusReporting;

public sealed record StoredManagerStatus(
    Guid RegistrationId, ManagerStatusReport Report, DateTimeOffset ReceivedAt, int IntervalSeconds);

public interface IManagerStatusStore
{
    Task UpsertAsync(
        Guid registrationId, ManagerStatusReport report, DateTimeOffset receivedAt, int intervalSeconds,
        CancellationToken cancellationToken);

    Task<StoredManagerStatus?> FindAsync(Guid registrationId, CancellationToken cancellationToken);

    Task<string?> FindVersionAsync(Guid registrationId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, StoredManagerStatus>> FindManyAsync(
        IReadOnlyCollection<Guid> registrationIds, CancellationToken cancellationToken);

    Task<int> DeleteAsync(Guid registrationId, CancellationToken cancellationToken);
}
