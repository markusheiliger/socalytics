using System.Net;
using System.Net.Sockets;
using Testcontainers.Nats;
using Xunit;

[assembly: AssemblyFixture(typeof(SocAlytics.Platform.Integration.Tests.Infrastructure.NatsContainerFixture))]

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed class NatsContainerFixture : IAsyncLifetime
{
    private NatsContainer? _container;
    private int _port;

    public string ConnectionString => $"nats://127.0.0.1:{_port}";

    public async ValueTask InitializeAsync()
    {
        _port = FreePort();
        await StartNewAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public Task StopAsync() => _container!.StopAsync();

    public Task StartAsync() => _container!.StartAsync();

    /// <summary>Replaces the container with an empty one on the same host port.</summary>
    public async Task ReplaceAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await StartNewAsync();
    }

    private async Task StartNewAsync()
    {
        _container = new NatsBuilder("nats:2.14")
            .WithPortBinding(_port, 4222)
            .WithCommand("-js")
            .Build();
        await _container.StartAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
