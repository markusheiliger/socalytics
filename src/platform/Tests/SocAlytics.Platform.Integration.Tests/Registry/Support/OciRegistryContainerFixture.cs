using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

/// <summary>A disposable anonymous <c>registry:2</c> reachable over HTTP; fails visibly when Docker is unavailable.</summary>
public sealed class OciRegistryContainerFixture : IAsyncLifetime
{
    private const int Port = 5000;

    private IContainer? _container;

    public string RegistryHost => _container is null
        ? throw new InvalidOperationException("The registry has not been started.")
        : $"{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder("registry:2")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/v2/").ForPort(Port)))
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
