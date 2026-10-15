using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class OidcProviderClientTests(OidcProviderContainerFixture container) : IClassFixture<OidcProviderContainerFixture>
{
	private const string Authority = "https://idp.test";
	private const string ClientId = "socalytics-client";
	private const string Redirect = "https://platform.test/api/v1/session/external/callback";
	private const string Nonce = "nonce-1";
	private const string Verifier = "verifier-0123456789012345678901234567890123456789";

	private static OidcProviderClient CreateClient(HttpMessageHandler handler, string authority, string authMethod = "client_secret_basic")
	{
		var options = new IdentityAccessOptions();
		options.ExternalProviders["test"] = new IdentityAccessOptions.ExternalProviderOptions
		{
			DisplayName = "Test",
			Authority = authority,
			ClientId = ClientId,
			ClientSecret = "secret-value",
			TokenEndpointAuthMethod = authMethod,
		};
		return new OidcProviderClient(Options.Create(options), new HttpClient(handler), NullLogger<OidcProviderClient>.Instance);
	}

	private static string Sign(RsaSecurityKey key, string issuer = Authority, string audience = ClientId,
		string? nonce = Nonce, DateTime? expires = null)
	{
		var claims = new Dictionary<string, object> { ["sub"] = "subject-1" };
		if (nonce is not null)
		{
			claims["nonce"] = nonce;
		}

		var notBefore = expires is { } e && e < DateTime.UtcNow ? e.AddHours(-1) : DateTime.UtcNow.AddMinutes(-1);
		return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			Claims = claims,
			NotBefore = notBefore,
			IssuedAt = notBefore,
			Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
			SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
		});
	}

	private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

	[Fact]
	public async Task ValidTokenReturnsIssuerAndSubjectOnly()
	{
		var key = NewKey("k1");
		var provider = new FakeProvider(key) { IdToken = Sign(key) };
		var client = CreateClient(provider, Authority);

		var identity = await client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None);

		identity.ShouldBe(new OidcIdentity(Authority, "subject-1"));
		provider.TokenRequestAuthorization.ShouldNotBeNull().Scheme.ShouldBe("Basic");
		provider.TokenRequestBody.ShouldContain("code_verifier=" + Verifier);
	}

	[Fact]
	public async Task PostAuthMethodSendsCredentialsInTheBody()
	{
		var key = NewKey("k1");
		var provider = new FakeProvider(key) { IdToken = Sign(key) };
		var client = CreateClient(provider, Authority, "client_secret_post");

		await client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None);

		provider.TokenRequestAuthorization.ShouldBeNull();
		provider.TokenRequestBody.ShouldContain("client_secret=secret-value");
	}

	[Fact]
	public async Task InvalidTokensAreRejected()
	{
		var key = NewKey("k1");
		var other = NewKey("k1");
		var cases = new Dictionary<string, string>
		{
			["issuer"] = Sign(key, issuer: "https://evil.test"),
			["audience"] = Sign(key, audience: "someone-else"),
			["signature"] = Sign(other),
			["expired"] = Sign(key, expires: DateTime.UtcNow.AddHours(-2)),
			["nonce"] = Sign(key, nonce: "other-nonce"),
			["missing nonce"] = Sign(key, nonce: null),
		};
		foreach (var (name, token) in cases)
		{
			var provider = new FakeProvider(key) { IdToken = token };
			var client = CreateClient(provider, Authority);

			var exception = await Should.ThrowAsync<OidcProviderException>(
				() => client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None), name);
			exception.Failure.ShouldBe(OidcProviderFailure.InvalidToken, name);
		}
	}

	[Fact]
	public async Task RotatedSigningKeyIsPickedUpOnUnknownKey()
	{
		var oldKey = NewKey("old");
		var provider = new FakeProvider(oldKey) { IdToken = Sign(oldKey) };
		var client = CreateClient(provider, Authority);
		await client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None);

		var newKey = NewKey("new");
		provider.Keys = [newKey];
		provider.IdToken = Sign(newKey);

		var identity = await client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None);
		identity.Subject.ShouldBe("subject-1");
	}

	[Fact]
	public async Task RejectedTokenRequestAndUnknownProviderFailDistinctly()
	{
		var provider = new FakeProvider(NewKey("k1")) { TokenStatus = HttpStatusCode.BadRequest };
		var client = CreateClient(provider, Authority);

		(await Should.ThrowAsync<OidcProviderException>(
			() => client.ExchangeCodeAsync("test", Redirect, "code", Verifier, Nonce, CancellationToken.None)))
			.Failure.ShouldBe(OidcProviderFailure.Rejected);
		(await Should.ThrowAsync<OidcProviderException>(
			() => client.ExchangeCodeAsync("nope", Redirect, "code", Verifier, Nonce, CancellationToken.None)))
			.Failure.ShouldBe(OidcProviderFailure.UnknownProvider);
	}

	[Fact]
	public async Task AuthorizationUrlCarriesS256PkceStateAndNonce()
	{
		var client = CreateClient(new FakeProvider(NewKey("k1")), Authority);

		var url = await client.BuildAuthorizationUrlAsync("test", Redirect, "state-1", Nonce, Verifier, CancellationToken.None);

		var query = System.Web.HttpUtility.ParseQueryString(url.Query);
		query["response_type"].ShouldBe("code");
		query["client_id"].ShouldBe(ClientId);
		query["redirect_uri"].ShouldBe(Redirect);
		query["scope"].ShouldBe("openid");
		query["state"].ShouldBe("state-1");
		query["nonce"].ShouldBe(Nonce);
		query["code_challenge_method"].ShouldBe("S256");
		query["code_challenge"].ShouldBe(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))));
		query["client_secret"].ShouldBeNull();
	}

	[Fact]
	public async Task UnreachableProviderIsReportedAsUnavailable()
	{
		var client = CreateClient(new FakeProvider(NewKey("k1")) { Offline = true }, Authority);

		(await Should.ThrowAsync<OidcProviderException>(
			() => client.BuildAuthorizationUrlAsync("test", Redirect, "s", Nonce, Verifier, CancellationToken.None)))
			.Failure.ShouldBe(OidcProviderFailure.Unavailable);
	}

	[Fact]
	public async Task RoundTripAgainstTheMockProviderReturnsIssuerAndSubject()
	{
		var client = CreateClient(new SocketsHttpHandler { AllowAutoRedirect = false }, container.Authority);
		var url = await client.BuildAuthorizationUrlAsync("test", Redirect, "state-1", Nonce, Verifier, CancellationToken.None);

		using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
		using var response = await browser.GetAsync(url, TestContext.Current.CancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.Found);
		var location = response.Headers.Location.ShouldNotBeNull();
		var query = System.Web.HttpUtility.ParseQueryString(location.Query);
		query["state"].ShouldBe("state-1");

		var identity = await client.ExchangeCodeAsync(
			"test", Redirect, query["code"].ShouldNotBeNull(), Verifier, Nonce, CancellationToken.None);

		identity.Issuer.ShouldBe(container.Authority);
		identity.Subject.ShouldBe(OidcProviderContainerFixture.Subject);
	}

	private sealed class FakeProvider(RsaSecurityKey key) : HttpMessageHandler
	{
		public RsaSecurityKey[] Keys { get; set; } = [key];
		public string IdToken { get; set; } = string.Empty;
		public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
		public bool Offline { get; set; }
		public System.Net.Http.Headers.AuthenticationHeaderValue? TokenRequestAuthorization { get; private set; }
		public string TokenRequestBody { get; private set; } = string.Empty;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (Offline)
			{
				throw new HttpRequestException("offline");
			}

			switch (request.RequestUri!.AbsolutePath)
			{
				case "/.well-known/openid-configuration":
					return Json(JsonSerializer.Serialize(new
					{
						issuer = Authority,
						authorization_endpoint = Authority + "/authorize",
						token_endpoint = Authority + "/token",
						jwks_uri = Authority + "/jwks",
					}));
				case "/jwks":
					var keys = Keys.Select(k =>
					{
						var p = k.Rsa.ExportParameters(false);
						return new
						{
							kty = "RSA",
							kid = k.KeyId,
							use = "sig",
							alg = SecurityAlgorithms.RsaSha256,
							n = Base64UrlEncoder.Encode(p.Modulus),
							e = Base64UrlEncoder.Encode(p.Exponent),
						};
					});
					return Json(JsonSerializer.Serialize(new { keys }));
				case "/token":
					TokenRequestAuthorization = request.Headers.Authorization;
					TokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
					return TokenStatus == HttpStatusCode.OK
						? Json(JsonSerializer.Serialize(new { id_token = IdToken, token_type = "Bearer" }))
						: new HttpResponseMessage(TokenStatus);
				default:
					return new HttpResponseMessage(HttpStatusCode.NotFound);
			}
		}

		private static HttpResponseMessage Json(string body) =>
			new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
	}
}
