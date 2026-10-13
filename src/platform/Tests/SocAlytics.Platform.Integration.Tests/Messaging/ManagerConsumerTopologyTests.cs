using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Messaging;

[Collection("NatsFaultInjection")]
public sealed class ManagerConsumerTopologyTests(NatsContainerFixture nats)
{
    private const string Stream = "SOCALYTICS_ANALYSIS_JOBS";
    private const string SegmentCapability = "dev-segment-capability";
    private const string MatchCapability = "dev-match-capability";

    private ServiceProvider Provider()
    {
        var settings = new Dictionary<string, string?>(TestWorkflowSettings.Values)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
            ["AnalystWork:ConsumerAckWait"] = "00:00:45",
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private static async Task<NatsJSContext> ProvisionAsync(ServiceProvider provider, CancellationToken ct)
    {
        var connection = provider.GetRequiredService<NatsConnectionProvider>();
        connection.EnsureStarted();
        await provider.GetRequiredService<NatsTopology>().EnsureAsync(ct);
        return new NatsJSContext(connection.Connection);
    }

    [Fact]
    public async Task Consumers_exist_after_provisioning_with_the_contract_configuration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Provider();
        var js = await ProvisionAsync(provider, ct);

        foreach (var capability in new[] { SegmentCapability, MatchCapability })
        {
            var consumer = await js.GetConsumerAsync(Stream, "analyst-" + capability, ct);
            var config = consumer.Info.Config;
            config.DurableName.ShouldBe("analyst-" + capability);
            config.FilterSubject.ShouldBe("analysis.jobs.ready." + capability);
            config.AckPolicy.ShouldBe(NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit);
            config.AckWait.ShouldBe(TimeSpan.FromSeconds(45));
            config.MaxDeliver.ShouldBe(-1);
        }
    }

    [Fact]
    public async Task Provisioning_twice_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Provider();
        var js = await ProvisionAsync(provider, ct);
        var first = await js.GetConsumerAsync(Stream, "analyst-" + SegmentCapability, ct);

        await provider.GetRequiredService<NatsTopology>().EnsureAsync(ct);

        var second = await js.GetConsumerAsync(Stream, "analyst-" + SegmentCapability, ct);
        second.Info.Created.ShouldBe(first.Info.Created);
        second.Info.Config.FilterSubject.ShouldBe(first.Info.Config.FilterSubject);
        second.Info.Config.AckWait.ShouldBe(first.Info.Config.AckWait);
    }

    [Fact]
    public async Task Ready_job_is_delivered_only_through_its_capability_consumer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Provider();
        var js = await ProvisionAsync(provider, ct);
        var payload = Guid.NewGuid().ToString("N");

        var published = await provider.GetRequiredService<IMessageTransport>().PublishAsync(
            new TransportMessage("analysis.jobs.ready." + MatchCapability, Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes(payload)), ct);
        published.ShouldBe(TransportPublishResult.Published);

        (await FetchAsync(js, SegmentCapability, payload, ct)).ShouldBeFalse();
        (await FetchAsync(js, MatchCapability, payload, ct)).ShouldBeTrue();
    }

    private static async Task<bool> FetchAsync(NatsJSContext js, string capability, string payload, CancellationToken ct)
    {
        var consumer = await js.GetConsumerAsync(Stream, "analyst-" + capability, ct);
        var found = false;
        await foreach (var message in consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = 50, Expires = TimeSpan.FromSeconds(3) },
            serializer: NatsRawSerializer<byte[]>.Default,
            cancellationToken: ct))
        {
            if (Encoding.UTF8.GetString(message.Data!) == payload)
            {
                found = true;
                await message.AckAsync(cancellationToken: ct);
            }
            else
            {
                await message.NakAsync(cancellationToken: ct);
            }
        }

        return found;
    }
}
