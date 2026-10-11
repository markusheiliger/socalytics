using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Abstractions;

public sealed record RegistrationPage(IReadOnlyList<AnalystManagerRegistration> Items, string? ContinuationToken);

public interface IRegistrationStore
{
    Task InsertAsync(
        AnalystManagerRegistration registration, byte[]? pairingCodeHash, byte[] pollingHandleHash,
        CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindByPairingCodeHashAsync(byte[] hash, CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindByPollingHandleHashAsync(byte[] hash, CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindByManagerIdAsync(Guid managerId, CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindNonTerminalByThumbprintAsync(string thumbprint, CancellationToken cancellationToken);

    Task<AnalystManagerRegistration?> FindLatestInactiveByThumbprintAsync(string thumbprint, CancellationToken cancellationToken);

    // Writes the mutable columns when the persisted state still equals expectedState.
    Task<VersionedWriteResult> UpdateAsync(
        AnalystManagerRegistration registration, RegistrationState expectedState, bool clearPairingCode,
        CancellationToken cancellationToken);

    Task<VersionedWriteResult> IncrementFingerprintMismatchAsync(
        Guid id, RegistrationState expectedState, CancellationToken cancellationToken);

    Task<VersionedWriteResult> SetChallengeAsync(
        Guid id, RegistrationState expectedState, byte[] challengeHash, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task<VersionedWriteResult> ClearChallengeAsync(
        Guid id, RegistrationState expectedState, CancellationToken cancellationToken);

    Task<RegistrationPage> ListAsync(
        RegistrationState? state, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<AnalystManagerRegistration>> ListDueForExpiryAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);
}
