namespace SocAlytics.Platform.Application.Registry.Abstractions;

public static class ProofRefusalReasons
{
    public const string Invalid = "proof-invalid";
    public const string Replayed = "proof-replayed";
}

// Never carries proof values; Challenge and KeyThumbprint are only set on success.
public sealed record ProofValidationResult(bool Succeeded, string? RefusalReason, string? Challenge = null, string? KeyThumbprint = null)
{
    public static ProofValidationResult Refused(string reason) => new(false, reason);
}

public interface IManagerProofValidator
{
    Task<ProofValidationResult> ValidateActivationProofAsync(string proof, string storedPublicJwk, CancellationToken cancellationToken);

    // audience defaults to the Analyst Manager token URI.
    Task<ProofValidationResult> ValidateClientAssertionAsync(
        string assertion, string expectedClientId, string storedPublicJwk, CancellationToken cancellationToken,
        Uri? audience = null);

    // requestPath is the request path; query and fragment are ignored. KeyThumbprint is the jkt of the header jwk.
    Task<ProofValidationResult> ValidateDPoPProofAsync(
        string proof, string httpMethod, string requestPath, string? accessToken, bool requireAccessTokenHash,
        CancellationToken cancellationToken);
}
