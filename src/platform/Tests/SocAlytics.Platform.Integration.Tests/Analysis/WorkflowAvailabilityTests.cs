using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class WorkflowAvailabilityTests
{
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
        definition.RequestedCapabilities.Count.ShouldBe(2);
        definition.SegmentationPolicy.Version.ShouldBe(1);
        definition.SegmentationPolicy.DurationSeconds.ShouldBe(300);
        definition.Nodes.Count.ShouldBe(2);
        var segment = definition.Nodes.Single(n => n.ExecutionScope == ExecutionScope.Segment);
        segment.Tier.ShouldBe(CapabilityTier.LowLevel);
        segment.MaxAttempts.ShouldBe(3);
        segment.LeaseSeconds.ShouldBe(30);
        segment.StaleTimeoutSeconds.ShouldBe(90);
        segment.ExecutionTimeoutSeconds.ShouldBe(3600);
        segment.ModelDigest.ShouldBeNull();
        var match = definition.Nodes.Single(n => n.ExecutionScope == ExecutionScope.Match);
        match.Tier.ShouldBe(CapabilityTier.HighLevel);
        match.ModelDigest.ShouldNotBeNull();
        match.Resources.ShouldContainKey("cpu");
        match.PolicyVersions.ShouldContainKey("retention");
        var edge = definition.Edges.ShouldHaveSingleItem();
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
