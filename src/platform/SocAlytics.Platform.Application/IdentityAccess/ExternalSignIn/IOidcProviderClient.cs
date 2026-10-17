namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public interface IOidcProviderClient
{
	/// <summary>Builds the authorization request URL with an <c>S256</c> PKCE challenge derived from <paramref name="codeVerifier"/>.</summary>
	/// <exception cref="OidcProviderException">The provider is unknown or its discovery document is unavailable.</exception>
	Task<Uri> BuildAuthorizationUrlAsync(
		string providerId, string redirectUri, string state, string nonce, string codeVerifier, CancellationToken cancellationToken);

	/// <summary>Exchanges the authorization code and validates the ID token; only issuer and subject are returned.</summary>
	/// <exception cref="OidcProviderException">The exchange or the ID-token validation failed.</exception>
	Task<OidcIdentity> ExchangeCodeAsync(
		string providerId, string redirectUri, string code, string codeVerifier, string expectedNonce, CancellationToken cancellationToken);
}

public sealed record OidcIdentity(string Issuer, string Subject);

public enum OidcProviderFailure
{
	UnknownProvider,
	Unavailable,
	Rejected,
	InvalidToken,
}

public sealed class OidcProviderException(OidcProviderFailure failure)
	: Exception($"The identity provider interaction failed ({failure}).")
{
	public OidcProviderFailure Failure { get; } = failure;
}
