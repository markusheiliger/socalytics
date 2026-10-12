using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis;

public sealed record MappedWorkflow(
    WorkflowGraph Graph,
    IReadOnlyList<PlannerNode> PlannerNodes,
    IReadOnlyDictionary<string, string> NodeSnapshotJson,
    string RunSnapshotJson,
    string RunSnapshotDigest);

public sealed class WorkflowSnapshotMapper(ICanonicalJson canonicalJson)
{
    public MappedWorkflow Map(ResolvedWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var graph = new WorkflowGraph(
            definition.Nodes.Select(n => new WorkflowNodeDefinition(n.NodeKey, n.CapabilityId, n.ExecutionScope)),
            definition.Edges.Select(e => new WorkflowEdgeDefinition(e.UpstreamNodeKey, e.DependentNodeKey, e.Kind, e.Predicate)));

        var planner = definition.Nodes.Select(n => new PlannerNode(
            n.NodeKey,
            n.ExecutionScope,
            new PinnedJobSnapshot(
                n.MaxAttempts, n.LeaseSeconds, n.StaleTimeoutSeconds, n.ExecutionTimeoutSeconds, n.AnalystProfileId,
                n.AnalystProfileDigest, n.ImageDigest, n.ModelDigest, n.Resources, n.SchemaVersion))).ToList();

        var nodeJson = definition.Nodes.ToDictionary(n => n.NodeKey, n => JsonSerializer.Serialize(NodeObject(n)), StringComparer.Ordinal);

        var snapshot = new Dictionary<string, object?>
        {
            ["workflowId"] = definition.WorkflowId,
            ["workflowVersion"] = definition.WorkflowVersion,
            ["requestedCapabilities"] = definition.RequestedCapabilities,
            ["completionPolicy"] = definition.CompletionPolicy,
            ["segmentationPolicy"] = new Dictionary<string, object?>
            {
                ["version"] = definition.SegmentationPolicy.Version,
                ["durationSeconds"] = definition.SegmentationPolicy.DurationSeconds,
                ["digest"] = definition.SegmentationPolicy.Digest.ToString(),
            },
            ["nodes"] = definition.Nodes.Select(NodeObject).ToList(),
            ["edges"] = definition.Edges.Select(e => new Dictionary<string, object?>
            {
                ["upstreamNodeKey"] = e.UpstreamNodeKey,
                ["dependentNodeKey"] = e.DependentNodeKey,
                ["kind"] = e.Kind,
                ["predicate"] = e.Predicate is null ? null : new Dictionary<string, object?>
                {
                    ["kind"] = e.Predicate.Kind,
                    ["capabilityId"] = e.Predicate.CapabilityId,
                    ["nodeKey"] = e.Predicate.NodeKey,
                },
            }).ToList(),
        };
        var element = JsonSerializer.SerializeToElement(snapshot);
        return new MappedWorkflow(graph, planner, nodeJson, element.GetRawText(), canonicalJson.Fingerprint(element));
    }

    private static Dictionary<string, object?> NodeObject(ResolvedWorkflowNode n) => new()
    {
        ["nodeKey"] = n.NodeKey,
        ["capabilityId"] = n.CapabilityId,
        ["capabilityVersion"] = n.CapabilityVersion,
        ["tier"] = AnalysisPersistedNames.ToPersistedName(n.Tier),
        ["executionScope"] = AnalysisPersistedNames.ToPersistedName(n.ExecutionScope),
        ["schemaVersion"] = n.SchemaVersion,
        ["analystProfileId"] = n.AnalystProfileId,
        ["analystProfileDigest"] = n.AnalystProfileDigest.ToString(),
        ["imageDigest"] = n.ImageDigest.ToString(),
        ["modelDigest"] = n.ModelDigest?.ToString(),
        ["policyVersions"] = n.PolicyVersions,
        ["resources"] = n.Resources,
        ["maxAttempts"] = n.MaxAttempts,
        ["leaseSeconds"] = n.LeaseSeconds,
        ["staleTimeoutSeconds"] = n.StaleTimeoutSeconds,
        ["executionTimeoutSeconds"] = n.ExecutionTimeoutSeconds,
    };
}
