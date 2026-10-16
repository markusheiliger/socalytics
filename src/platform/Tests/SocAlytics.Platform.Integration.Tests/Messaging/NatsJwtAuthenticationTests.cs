using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Messaging;

public sealed class NatsJwtAuthenticationTests(NatsJwtContainerFixture nats) : IClassFixture<NatsJwtContainerFixture>
{
    private ServiceProvider Provider()
    {
        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddTestHostEnvironment();
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Platform_provisions_publishes_and_consumes_in_jwt_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Provider();
        var connection = provider.GetRequiredService<NatsConnectionProvider>();
        connection.EnsureStarted();

        await provider.GetRequiredService<NatsTopology>().EnsureAsync(ct);

        var transport = provider.GetRequiredService<IMessageTransport>();
        var ready = await transport.PublishAsync(
            new TransportMessage("analysis.jobs.ready.test", Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes("{}")), ct);
        ready.ShouldBe(TransportPublishResult.Published);
        await transport.PublishAsync(
            new TransportMessage("matches.recordings-finalized", Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes("{\"a\":1}")), ct);

        var js = new NatsJSContext(connection.Connection);
        var consumer = await js.GetConsumerAsync("SOCALYTICS_EVENTS", "analysis-scheduler-recordings-finalized", ct);
        await foreach (var message in consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(5) },
            serializer: NatsRawSerializer<byte[]>.Default,
            cancellationToken: ct))
        {
            await message.AckAsync(cancellationToken: ct);
            Encoding.UTF8.GetString(message.Data!).ShouldBe("{\"a\":1}");
            return;
        }

        Assert.Fail("The notification was not delivered.");
    }

    [Fact]
    public async Task Connection_without_credentials_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NatsConnection(NatsOpts.Default with
        {
            Url = nats.ConnectionString,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxReconnectRetry = 1,
        });

        await Should.ThrowAsync<Exception>(async () => await connection.PingAsync(ct));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void None_is_refused_outside_development_and_test(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:nats"] = "nats://127.0.0.1:4222" })
            .Build();
        var trust = new NatsTrust(Microsoft.Extensions.Options.Options.Create(new NatsAuthenticationOptions()));

        Should.Throw<InvalidOperationException>(() =>
            new NatsConnectionProvider(configuration, NullLogger<NatsConnectionProvider>.Instance, new TestEnvironment(environment), trust))
            .Message.ShouldContain("Development and Test");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task None_is_accepted_in_development_and_test(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:nats"] = "nats://127.0.0.1:4222" })
            .Build();
        var trust = new NatsTrust(Microsoft.Extensions.Options.Options.Create(new NatsAuthenticationOptions()));

        await using var provider =
            new NatsConnectionProvider(configuration, NullLogger<NatsConnectionProvider>.Instance, new TestEnvironment(environment), trust);
        provider.IsConfigured.ShouldBeTrue();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
