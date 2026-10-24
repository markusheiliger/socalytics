using System.Text.Json;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure.Contracts;
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class WorkflowSnapshotVideoPolicyTests
{
    private static readonly string Hex = new('a', 64);

    private static ResolvedWorkflowNode Node(string key, string videoPolicy) => new(
        key, "cap-" + key, "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, "1.0.0", "profile-" + key,
        Sha256Digest.Parse("sha-256:" + Hex), Sha256Digest.Parse("sha-256:" + Hex), null,
        new Dictionary<string, string>(), new Dictionary<string, string>(), 3, 60, 120, 600, videoPolicy);

    private static ResolvedWorkflowDefinition Definition(params ResolvedWorkflowNode[] nodes) => new(
        "wf", "1", nodes.Select(n => n.CapabilityId).ToArray(), WorkflowCompletionPolicies.AllRequestedCapabilities,
        new SegmentationPolicy(1, 300, Sha256Digest.Parse("sha-256:" + Hex)), nodes, []);

    private static WorkflowSnapshotMapper Mapper() => new(new JsonCanonicalizer());

    [Theory]
    [InlineData(VideoPolicies.Required)]
    [InlineData(VideoPolicies.Optional)]
    [InlineData(VideoPolicies.Forbidden)]
    public void Node_snapshot_carries_the_pinned_video_policy(string policy)
    {
        var mapped = Mapper().Map(Definition(Node("a", policy)));

        using var nodeJson = JsonDocument.Parse(mapped.NodeSnapshotJson["a"]);
        nodeJson.RootElement.GetProperty("videoPolicy").GetString().ShouldBe(policy);
        using var run = JsonDocument.Parse(mapped.RunSnapshotJson);
        run.RootElement.GetProperty("nodes")[0].GetProperty("videoPolicy").GetString().ShouldBe(policy);
    }

    [Fact]
    public void Each_node_keeps_its_own_policy_and_the_run_digest_depends_on_it()
    {
        var mapper = Mapper();
        var mixed = mapper.Map(Definition(Node("a", VideoPolicies.Required), Node("b", VideoPolicies.Forbidden)));

        using var a = JsonDocument.Parse(mixed.NodeSnapshotJson["a"]);
        using var b = JsonDocument.Parse(mixed.NodeSnapshotJson["b"]);
        a.RootElement.GetProperty("videoPolicy").GetString().ShouldBe(VideoPolicies.Required);
        b.RootElement.GetProperty("videoPolicy").GetString().ShouldBe(VideoPolicies.Forbidden);

        var changed = mapper.Map(Definition(Node("a", VideoPolicies.Optional), Node("b", VideoPolicies.Forbidden)));
        changed.RunSnapshotDigest.ShouldNotBe(mixed.RunSnapshotDigest);
    }

    [Fact]
    public async Task Configured_resolver_exposes_the_policy_and_rejects_unknown_values()
    {
        var ct = TestContext.Current.CancellationToken;

        ConfiguredWorkflowOptions Options(string? policy)
        {
            var node = new ConfiguredNode
            {
                NodeKey = "a",
                CapabilityId = "cap-a",
                CapabilityVersion = "1.0.0",
                Tier = "low-level",
                ExecutionScope = "segment",
                SchemaVersion = "1.0.0",
                AnalystProfileId = "profile-a",
                AnalystProfileDigest = "sha-256:" + Hex,
                ImageDigest = "sha-256:" + Hex,
                MaxAttempts = 3,
                LeaseSeconds = 60,
                StaleTimeoutSeconds = 120,
                ExecutionTimeoutSeconds = 600,
            };
            if (policy is not null)
            {
                node.VideoPolicy = policy;
            }

            return new ConfiguredWorkflowOptions
            {
                WorkflowId = "wf",
                WorkflowVersion = "1",
                RequestedCapabilities = ["cap-a"],
                SegmentationPolicy = new ConfiguredSegmentationPolicy { Version = 1, DurationSeconds = 300, Digest = "sha-256:" + Hex },
                Nodes = [node],
            };
        }

        static ConfiguredWorkflowDefinitionResolver Resolver(ConfiguredWorkflowOptions o) => new(new FixedMonitor(o));

        var forbidden = await Resolver(Options(VideoPolicies.Forbidden)).ResolveDefaultAsync(ct);
        forbidden.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition.Nodes[0].VideoPolicy.ShouldBe(VideoPolicies.Forbidden);

        var unset = await Resolver(Options(null)).ResolveDefaultAsync(ct);
        unset.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition.Nodes[0].VideoPolicy.ShouldBe(VideoPolicies.Optional);

        var unknown = await Resolver(Options("sometimes")).ResolveDefaultAsync(ct);
        unknown.ShouldBeOfType<WorkflowResolution.UnavailableResult>().Category.ShouldBe(WorkflowResolution.InvalidDefinition);
    }

    private sealed class FixedMonitor(ConfiguredWorkflowOptions value) : IOptionsMonitor<ConfiguredWorkflowOptions>
    {
        public ConfiguredWorkflowOptions CurrentValue => value;

        public ConfiguredWorkflowOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ConfiguredWorkflowOptions, string?> listener) => null;
    }
}
