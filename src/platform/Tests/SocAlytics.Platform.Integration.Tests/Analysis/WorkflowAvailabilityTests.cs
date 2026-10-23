using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class WorkflowAvailabilityTests(
    SocAlytics.Platform.Integration.Tests.Infrastructure.PostgresContainerFixture postgres,
    SocAlytics.Platform.Integration.Tests.Recordings.RustFsContainerFixture rustFs,
    SocAlytics.Platform.Integration.Tests.Infrastructure.NatsContainerFixture nats)
{
    private sealed class Switch
    {
        public volatile bool Down;
    }

    private sealed class SwitchResolver(IWorkflowDefinitionResolver inner, Switch flag) : IWorkflowDefinitionResolver
    {
        public Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken cancellationToken) =>
            flag.Down
                ? Task.FromResult(WorkflowResolution.Unavailable(WorkflowResolution.NotConfigured))
                : inner.ResolveDefaultAsync(cancellationToken);
    }

    private Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:nats"] = nats.ConnectionString,
        ["Messaging:Nats:ProvisionTopology"] = "true",
        ["Analysis:Workers:Enabled"] = "true",
        ["Analysis:Notifications:NakDelaySeconds"] = "1",
        ["Analysis:Notifications:ResolveRecheckSeconds"] = "1",
    };

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        for (var i = 0; i < 120; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    // The NATS container is shared by the whole NatsFaultInjection collection, and its events stream and durable
    // finalization consumer keep notifications that earlier tests left behind for recording sets that do not exist
    // in this test's database. This test's consumer would fetch them after recovery and add receipts or consume
    // fetch windows before the one expected notification. Deleting the stream (and with it the consumer) gives the
    // test its own notification state; the topology recreates both when the platform starts.
    // Verification: 20 of 20 solo runs and 5 of 5 runs of this class under CPU load (one busy loop per core) passed;
    // a probe that made the workflow resolvable before the unresolvable assertions and a probe that finalized a
    // second recording set after recovery each failed the test on its assertions; both probes were removed.
    private async Task ResetEventsStreamAsync(CancellationToken ct)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = nats.ConnectionString });
        try
        {
            await new NatsJSContext(connection).DeleteStreamAsync("SOCALYTICS_EVENTS", ct);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
        }
    }

    // True once the events stream holds the finalization notification and the durable consumer reports it pending
    // (not yet fetched, or delivered and negatively acknowledged) with nothing acknowledged, which is the state the
    // platform keeps while the workflow is unresolvable.
    private async Task<bool> NotificationHeldBackAsync(CancellationToken ct)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = nats.ConnectionString });
        var js = new NatsJSContext(connection);
        var stream = await js.GetStreamAsync("SOCALYTICS_EVENTS", cancellationToken: ct);
        if (stream.Info.State.Messages < 1)
        {
            return false;
        }

        var consumer = await js.GetConsumerAsync("SOCALYTICS_EVENTS", "analysis-scheduler-recordings-finalized", ct);
        return consumer.Info.AckFloor.StreamSeq == 0 && consumer.Info.NumPending + (ulong)consumer.Info.NumAckPending >= 1;
    }

    [Fact]
    public async Task Missing_definition_reports_unhealthy_and_keeps_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SocAlytics.Platform.Integration.Tests.Recordings.Support.FinalizationSetup.StartAsync(
            postgres, rustFs, ct, extraSettings: Settings(), omitWorkflow: true);
        using var client = s.Factory.CreateApiClient();
        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/alive", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var health = s.Factory.Services.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        var report = await health.CheckHealthAsync(r => r.Name == "analysis-workflow", ct);
        var entry = report.Entries["analysis-workflow"];
        entry.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        entry.Description.ShouldBe("not-configured");
        entry.Data.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var flag = new Switch { Down = true };
        await ResetEventsStreamAsync(ct);
        await using var s = await SocAlytics.Platform.Integration.Tests.Recordings.Support.FinalizationSetup.StartAsync(
            postgres, rustFs, ct, extraSettings: Settings(), configureServices: services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IWorkflowDefinitionResolver));
                services.Remove(original);
                services.AddSingleton<IWorkflowDefinitionResolver>(sp => new SwitchResolver(
                    (IWorkflowDefinitionResolver)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), flag));
            });
        await SocAlytics.Platform.Integration.Tests.Recordings.Support.RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
        (await WaitAsync(async () => await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'matches.recordings-finalized' AND state = 'published'", ct) == 1, ct)).ShouldBeTrue();
        (await WaitAsync(() => NotificationHeldBackAsync(ct), ct)).ShouldBeTrue();
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct)).ShouldBe(0);
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(0);

        var health = s.Factory.Services.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        (await health.CheckHealthAsync(r => r.Name == "analysis-workflow", ct)).Status
            .ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);

        flag.Down = false;
        (await WaitAsync(async () =>
            await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct) >= 1
            && await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct) >= 1, ct)).ShouldBeTrue();
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
        (await health.CheckHealthAsync(r => r.Name == "analysis-workflow", ct)).Status
            .ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct)).ShouldBe(1);
    }

    private static string DevelopmentSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("appsettings.Development.json not found.");
    }

    private static (ServiceProvider Provider, IConfigurationRoot Root) Create(Dictionary<string, string?>? overrides = null, bool removeSection = false)
    {
        var builder = new ConfigurationBuilder();
        if (!removeSection)
        {
            builder.AddJsonFile(DevelopmentSettingsPath(), optional: false, reloadOnChange: false);
        }

        if (overrides is not null)
        {
            builder.AddInMemoryCollection(overrides);
        }

        var root = builder.Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(root);
        services.AddOptions<ConfiguredWorkflowOptions>().BindConfiguration(ConfiguredWorkflowOptions.SectionName);
        services.AddSingleton<IWorkflowDefinitionResolver, ConfiguredWorkflowDefinitionResolver>();
        services.AddSingleton<ICanonicalJson, SocAlytics.Platform.Infrastructure.Contracts.JsonCanonicalizer>();
        return (services.BuildServiceProvider(), root);
    }

    private static async Task<WorkflowResolution> Resolve(ServiceProvider p) =>
        await p.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(CancellationToken.None);

    [Fact]
    public async Task Development_fixture_resolves_with_all_fields()
    {
        var (provider, _) = Create();
        var result = await Resolve(provider);

        var definition = result.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;
        definition.WorkflowId.ShouldBe("development-default");
        definition.WorkflowVersion.ShouldBe("1");
        definition.CompletionPolicy.ShouldBe("all-requested-capabilities");
        definition.RequestedCapabilities.Count.ShouldBe(3);
        definition.SegmentationPolicy.Version.ShouldBe(1);
        definition.SegmentationPolicy.DurationSeconds.ShouldBe(300);
        definition.Nodes.Count.ShouldBe(3);
        var segment = definition.Nodes.Single(n => n.ExecutionScope == ExecutionScope.Segment);
        segment.Tier.ShouldBe(CapabilityTier.LowLevel);
        segment.MaxAttempts.ShouldBe(3);
        segment.LeaseSeconds.ShouldBe(30);
        segment.StaleTimeoutSeconds.ShouldBe(90);
        segment.ExecutionTimeoutSeconds.ShouldBe(3600);
        segment.ModelDigest.ShouldBeNull();
        var match = definition.Nodes.Single(n => n.NodeKey == "dev-match-node");
        match.Tier.ShouldBe(CapabilityTier.HighLevel);
        match.ModelDigest.ShouldNotBeNull();
        match.Resources.ShouldContainKey("cpu");
        match.PolicyVersions.ShouldContainKey("retention");
        definition.Edges.Count.ShouldBe(2);
        var edge = definition.Edges.Single(e => e.DependentNodeKey == match.NodeKey);
        edge.Kind.ShouldBe("required");
        edge.UpstreamNodeKey.ShouldBe(segment.NodeKey);
        edge.DependentNodeKey.ShouldBe(match.NodeKey);
    }

    [Fact]
    public async Task Removed_section_is_not_configured()
    {
        var (cleared, _) = Create(removeSection: true);
        var result = await Resolve(cleared);
        result.ShouldBeOfType<WorkflowResolution.UnavailableResult>().Category.ShouldBe("not-configured");
    }

    [Fact]
    public async Task Malformed_digest_is_invalid_definition()
    {
        var (provider, _) = Create(new() { ["Analysis:DefaultWorkflow:Nodes:0:ImageDigest"] = "sha-256:xyz" });
        var result = await Resolve(provider);
        result.ShouldBeOfType<WorkflowResolution.UnavailableResult>().Category.ShouldBe("invalid-definition");
    }

    [Fact]
    public async Task Configuration_reload_is_observed()
    {
        var (provider, root) = Create(new());
        (await Resolve(provider)).ShouldBeOfType<WorkflowResolution.ResolvedResult>();

        root["Analysis:DefaultWorkflow:WorkflowVersion"] = "2";
        root.Reload();

        var definition = (await Resolve(provider)).ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;
        definition.WorkflowVersion.ShouldBe("2");
    }

    [Fact]
    public async Task Fingerprint_is_stable_across_property_order()
    {
        var (provider, _) = Create();
        var definition = ((WorkflowResolution.ResolvedResult)await Resolve(provider)).Definition;
        var json = provider.GetRequiredService<ICanonicalJson>();

        var element = JsonSerializer.SerializeToElement(definition, new JsonSerializerOptions { Converters = { new DigestConverter() } });
        var reordered = JsonSerializer.SerializeToElement(Reverse(element));

        json.Fingerprint(element).ShouldBe(json.Fingerprint(reordered));
    }

    private static object? Reverse(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Reverse().ToDictionary(p => p.Name, p => Reverse(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Reverse).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetInt64(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private sealed class DigestConverter : System.Text.Json.Serialization.JsonConverter<SocAlytics.Platform.Domain.Recordings.Sha256Digest>
    {
        public override SocAlytics.Platform.Domain.Recordings.Sha256Digest Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            SocAlytics.Platform.Domain.Recordings.Sha256Digest.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, SocAlytics.Platform.Domain.Recordings.Sha256Digest value, JsonSerializerOptions o) =>
            writer.WriteStringValue(value.ToString());
    }
}
