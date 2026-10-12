using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class ConfiguredWorkflowDefinitionResolver(IOptionsMonitor<ConfiguredWorkflowOptions> options) : IWorkflowDefinitionResolver
{
    public Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configured = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(configured.WorkflowId) && configured.Nodes.Count == 0)
            {
                return Task.FromResult(WorkflowResolution.Unavailable(WorkflowResolution.NotConfigured));
            }

            var definition = Map(configured);
            return Task.FromResult(definition is null
                ? WorkflowResolution.Unavailable(WorkflowResolution.InvalidDefinition)
                : WorkflowResolution.Resolved(definition));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(WorkflowResolution.Unavailable(WorkflowResolution.InvalidDefinition));
        }
    }

    private static ResolvedWorkflowDefinition? Map(ConfiguredWorkflowOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.WorkflowId)
            || string.IsNullOrWhiteSpace(o.WorkflowVersion)
            || o.CompletionPolicy != WorkflowCompletionPolicies.AllRequestedCapabilities
            || o.SegmentationPolicy.DurationSeconds <= 0
            || !Sha256Digest.TryParse(o.SegmentationPolicy.Digest, out var segmentationDigest))
        {
            return null;
        }

        var nodes = new List<ResolvedWorkflowNode>();
        foreach (var n in o.Nodes)
        {
            if (!AnalysisPersistedNames.TryParseCapabilityTier(n.Tier, out var tier)
                || !AnalysisPersistedNames.TryParseExecutionScope(n.ExecutionScope, out var scope)
                || !Sha256Digest.TryParse(n.AnalystProfileDigest, out var profileDigest)
                || !Sha256Digest.TryParse(n.ImageDigest, out var imageDigest))
            {
                return null;
            }

            Sha256Digest? modelDigest = null;
            if (n.ModelDigest is not null && !Sha256Digest.TryParse(n.ModelDigest, out modelDigest))
            {
                return null;
            }

            nodes.Add(new ResolvedWorkflowNode(
                n.NodeKey,
                n.CapabilityId,
                n.CapabilityVersion,
                tier,
                scope,
                n.SchemaVersion,
                n.AnalystProfileId,
                profileDigest,
                imageDigest,
                modelDigest,
                new Dictionary<string, string>(n.PolicyVersions, StringComparer.Ordinal),
                new Dictionary<string, string>(n.Resources, StringComparer.Ordinal),
                n.MaxAttempts,
                n.LeaseSeconds,
                n.StaleTimeoutSeconds,
                n.ExecutionTimeoutSeconds));
        }

        var edges = o.Edges
            .Select(e => new ResolvedWorkflowEdge(
                e.UpstreamNodeKey,
                e.DependentNodeKey,
                e.Kind,
                e.Predicate is null ? null : new ConditionalPredicate(e.Predicate.Kind, e.Predicate.CapabilityId, e.Predicate.NodeKey)))
            .ToArray();

        return new ResolvedWorkflowDefinition(
            o.WorkflowId,
            o.WorkflowVersion,
            o.RequestedCapabilities.ToArray(),
            o.CompletionPolicy,
            new SegmentationPolicy(o.SegmentationPolicy.Version, o.SegmentationPolicy.DurationSeconds, segmentationDigest),
            nodes,
            edges);
    }
}

internal sealed class ConfiguredWorkflowOptions
{
    public const string SectionName = "Analysis:DefaultWorkflow";

    public string WorkflowId { get; set; } = "";

    public string WorkflowVersion { get; set; } = "";

    public List<string> RequestedCapabilities { get; set; } = [];

    public string CompletionPolicy { get; set; } = WorkflowCompletionPolicies.AllRequestedCapabilities;

    public ConfiguredSegmentationPolicy SegmentationPolicy { get; set; } = new();

    public List<ConfiguredNode> Nodes { get; set; } = [];

    public List<ConfiguredEdge> Edges { get; set; } = [];
}

internal sealed class ConfiguredSegmentationPolicy
{
    public int Version { get; set; }

    public int DurationSeconds { get; set; }

    public string? Digest { get; set; }
}

internal sealed class ConfiguredNode
{
    public string NodeKey { get; set; } = "";

    public string CapabilityId { get; set; } = "";

    public string CapabilityVersion { get; set; } = "";

    public string? Tier { get; set; }

    public string? ExecutionScope { get; set; }

    public string SchemaVersion { get; set; } = "";

    public string AnalystProfileId { get; set; } = "";

    public string? AnalystProfileDigest { get; set; }

    public string? ImageDigest { get; set; }

    public string? ModelDigest { get; set; }

    public Dictionary<string, string> PolicyVersions { get; set; } = [];

    public Dictionary<string, string> Resources { get; set; } = [];

    public int MaxAttempts { get; set; }

    public int LeaseSeconds { get; set; }

    public int StaleTimeoutSeconds { get; set; }

    public int ExecutionTimeoutSeconds { get; set; }
}

internal sealed class ConfiguredEdge
{
    public string UpstreamNodeKey { get; set; } = "";

    public string DependentNodeKey { get; set; } = "";

    public string Kind { get; set; } = "";

    public ConfiguredPredicate? Predicate { get; set; }
}

internal sealed class ConfiguredPredicate
{
    public string Kind { get; set; } = "";

    public string? CapabilityId { get; set; }

    public string? NodeKey { get; set; }
}
