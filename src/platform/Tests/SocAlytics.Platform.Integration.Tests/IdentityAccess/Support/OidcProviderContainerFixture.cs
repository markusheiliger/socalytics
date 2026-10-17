using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

/// <summary>
/// A disposable <c>mock-oauth2-server</c> issuing an ID token with subject <see cref="Subject"/> without an interactive
/// login; fails visibly when Docker is unavailable.
/// </summary>
public sealed class OidcProviderContainerFixture : IAsyncLifetime
{
	public const string Subject = "mock-subject-1";
	public const string IssuerId = "test";

	private const string Image =
		"ghcr.io/navikt/mock-oauth2-server@sha256:65d4ed47ce094cc3849e41e03499bfb32372d69c726ec22b3d5a19eba9bc787e";

	private const string Config = """
		{"interactiveLogin":false,"httpServer":"NettyWrapper","tokenCallbacks":[{"issuerId":"test","requestMappings":[{"requestParam":"grant_type","match":"authorization_code","claims":{"sub":"mock-subject-1"}}]}]}
		""";

	private const int Port = 8080;

	private IContainer? _container;

	public string Authority => _container is null
		? throw new InvalidOperationException("The provider has not been started.")
		: $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/{IssuerId}";

	public async ValueTask InitializeAsync()
	{
		_container = new ContainerBuilder(Image)
			.WithPortBinding(Port, true)
			.WithEnvironment("JSON_CONFIG", Config)
			.WithWaitStrategy(Wait.ForUnixContainer()
				.UntilHttpRequestIsSucceeded(request => request.ForPath($"/{IssuerId}/.well-known/openid-configuration").ForPort(Port)))
			.Build();
		await _container.StartAsync(CancellationToken.None);
	}

	public async ValueTask DisposeAsync()
	{
		if (_container is not null)
		{
			await _container.DisposeAsync();
		}
	}
}
