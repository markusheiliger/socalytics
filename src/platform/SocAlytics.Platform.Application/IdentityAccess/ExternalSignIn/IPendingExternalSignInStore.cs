namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public sealed record PendingExternalSignIn(
    Guid Id,
    byte[] StateHash,
    byte[] BrowserBindingHash,
    string Nonce,
    string CodeVerifier,
    string ProviderId,
    Guid? LinkCredentialId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public interface IPendingExternalSignInStore
{
    Task CreateAsync(PendingExternalSignIn pending, CancellationToken cancellationToken);

    /// <summary>Consumes the pending sign-in with this state exactly once; null when unknown, expired, or already consumed.</summary>
    Task<PendingExternalSignIn?> ConsumeAsync(byte[] stateHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Deletes at most <paramref name="limit"/> expired or consumed rows and returns the number deleted.</summary>
    Task<int> DeleteExpiredOrConsumedAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}
