using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.JetStream;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class TransportRecoveryTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartHostAsync(string? natsConnection, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = new PlatformApiFactory(
            db,
            clubBootstrap: new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "Transport Club",
                ["FirstClubAdmin:AccountName"] = "transport-admin",
                ["FirstClubAdmin:InitialPassword"] = "Transport-Initial-Pass-1234",
            },
            recordingUpload: RecordingTestData.WithKeyPrefix(RecordingTestData.NewKeyPrefix()),
            objectStorage: new Dictionary<string, string?>
            {
                ["ServiceUrl"] = rustFs.ServiceUrl,
                ["Region"] = rustFs.Region,
                ["AccessKey"] = rustFs.AccessKey,
                ["SecretKey"] = rustFs.SecretKey,
                ["Bucket"] = rustFs.Bucket,
            },
            extraSettings: natsConnection is null ? null : new Dictionary<string, string?>
            {
                ["ConnectionStrings:nats"] = natsConnection,
                ["Messaging:Nats:ProvisionTopology"] = "true",
            });

        return (db, factory);
    }

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

    private static async Task<HealthReportEntry> NatsEntryAsync(PlatformApiFactory factory, CancellationToken ct)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "nats", ct);
        return report.Entries["nats"];
    }

    [Fact]
    public async Task Without_connection_string_no_nats_check_is_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartHostAsync(null, ct);
        await using var _ = db;
        await using var __ = factory;
        factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value
            .Registrations.ShouldNotContain(r => r.Name == "nats");
        using var client = factory.CreateApiClient();
        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await client.GetStringAsync("/health", ct)).ShouldBe("Healthy");
        (await client.GetAsync("/alive", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_follows_the_connection_and_signals_reconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartHostAsync(nats.ConnectionString, ct);
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateApiClient();
        var signal = factory.Services.GetRequiredService<TransportReconnectSignal>();
        (await NatsEntryAsync(factory, ct)).Status.ShouldBe(HealthStatus.Healthy);

        var before = signal.Count;
        await nats.StopAsync();
        try
        {
            var entry = await NatsEntryAsync(factory, ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (entry.Status != HealthStatus.Degraded && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
                entry = await NatsEntryAsync(factory, ct);
            }

            entry.Status.ShouldBe(HealthStatus.Degraded);
            entry.Description.ShouldBe("transport-unavailable");
            using var response = await client.GetAsync("/health", ct);
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).ShouldBe("Degraded");
        }
        finally
        {
            await nats.StartAsync();
        }

        var recovered = DateTime.UtcNow.AddSeconds(60);
        while (signal.Count == before && DateTime.UtcNow < recovered)
        {
            await Task.Delay(200, ct);
        }

        signal.Count.ShouldBe(before + 1);
        (await NatsEntryAsync(factory, ct)).Status.ShouldBe(HealthStatus.Healthy);
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
