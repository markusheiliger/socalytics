namespace SocAlytics.Platform.Application.Registry.QueueCredentials;

public sealed record IssuedQueueCredential(
    Guid QueueCredentialId,
    Guid RegistrationId,
    string UserPublicKey,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? RevocationPushedAt);

public interface IQueueCredentialStore
{
    /// <summary>Records an issuance; returns false when the user public key was already issued.</summary>
    Task<bool> RecordIssuanceAsync(IssuedQueueCredential credential, CancellationToken cancellationToken);

    Task<IReadOnlyList<IssuedQueueCredential>> ListUnexpiredAsync(Guid registrationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks every unexpired, not yet revoked credential of the registration as revoked and returns the count.</summary>
    Task<int> MarkRevokedAsync(Guid registrationId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<IssuedQueueCredential>> ListRevokedUnexpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the given credentials as contained in an acknowledged account JWT and returns the count.</summary>
    Task<int> MarkPushedAsync(IReadOnlyCollection<Guid> queueCredentialIds, DateTimeOffset now, CancellationToken cancellationToken);
}
