namespace SocAlytics.Platform.Application.Registry.Abstractions;

public static class ProofKinds
{
    public const string Activation = "activation";
    public const string ClientAssertion = "client_assertion";
    public const string DPoP = "dpop";
}

public interface IProofReplayStore
{
    // Returns false when the jti hash was already recorded for the kind.
    Task<bool> TryRecordAsync(string kind, byte[] jtiHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
