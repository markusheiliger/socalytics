using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class TransportRecoveryTests(NatsContainerFixture nats)
{
    private static ServiceProvider Build(string? connectionString)
    {
        var settings = new Dictionary<string, string?> { ["Messaging:Nats:ProvisionTopology"] = "true" };
        if (connectionString is not null)
        {
            settings["ConnectionStrings:nats"] = connectionString;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddOptions<NatsOptions>().BindConfiguration(NatsOptions.SectionName);
        services.AddSingleton<NatsConnectionProvider>();
        services.AddSingleton<NatsTopology>();
        services.AddSingleton<IHostedService, NatsConnectionStarter>();
        services.AddSingleton<IMessageTransport, NatsJetStreamTransport>();
        return services.BuildServiceProvider();
    }

    private static TransportMessage Message(string id) =>
        new("matches.recordings-finalized", id, Encoding.UTF8.GetBytes("{\"a\":1}"));

    [Fact]
    public async Task Hosted_service_connects_without_any_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var connection = provider.GetRequiredService<NatsConnectionProvider>();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!connection.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
        }

        connection.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task Topology_provisioning_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var topology = provider.GetRequiredService<NatsTopology>();
        await topology.EnsureAsync(ct);
        await topology.EnsureAsync(ct);

        var js = new NatsJSContext(provider.GetRequiredService<NatsConnectionProvider>().Connection);
        var stream = await js.GetStreamAsync("SOCALYTICS_EVENTS", cancellationToken: ct);
        stream.Info.Config.DuplicateWindow.ShouldBe(TimeSpan.FromSeconds(120));
        await js.GetStreamAsync("SOCALYTICS_ANALYSIS_JOBS", cancellationToken: ct);
    }

    [Fact]
    public async Task Publish_lands_on_the_stream_and_a_repeated_id_is_a_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var transport = provider.GetRequiredService<IMessageTransport>();
        var id = Guid.NewGuid().ToString();

        (await transport.PublishAsync(Message(id), ct)).ShouldBe(TransportPublishResult.Published);
        (await transport.PublishAsync(Message(id), ct)).ShouldBe(TransportPublishResult.Duplicate);

        var js = new NatsJSContext(provider.GetRequiredService<NatsConnectionProvider>().Connection);
        var stream = await js.GetStreamAsync("SOCALYTICS_EVENTS", cancellationToken: ct);
        (stream.Info.State.Messages > 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_connection_string_yields_transport_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(null);
        provider.GetRequiredService<NatsConnectionProvider>().IsConfigured.ShouldBeFalse();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        var ex = await Should.ThrowAsync<MessageTransportException>(
            () => provider.GetRequiredService<IMessageTransport>().PublishAsync(Message("x"), ct));
        ex.FailureCategory.ShouldBe("transport-unavailable");
    }

    [Fact]
    public async Task Stopped_container_yields_transport_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var transport = provider.GetRequiredService<IMessageTransport>();
        await transport.PublishAsync(Message(Guid.NewGuid().ToString()), ct);

        await nats.StopAsync();
        try
        {
            var ex = await Should.ThrowAsync<MessageTransportException>(
                () => transport.PublishAsync(Message(Guid.NewGuid().ToString()), ct));
            ex.FailureCategory.ShouldBeOneOf("transport-unavailable", "publish-timeout");
        }
        finally
        {
            await nats.StartAsync();
        }
    }
}
