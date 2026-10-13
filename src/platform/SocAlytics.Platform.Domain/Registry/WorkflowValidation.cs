using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public sealed record WorkflowNodeInput(string NodeKey, AnalystProfile Profile, CapabilityDeclaration Declaration);

public sealed record SegmentationPolicyInput(int DurationSeconds, string? Digest);

public sealed record WorkflowInput(
    IReadOnlyList<string> RequestedCapabilities,
    string CompletionPolicy,
    SegmentationPolicyInput SegmentationPolicy,
    IReadOnlyList<WorkflowNodeInput> Nodes,
    IReadOnlyList<WorkflowEdgeDefinition> Edges);

public sealed record WorkflowViolation(string Code, string Message);

public static class WorkflowValidation
{
    public const string ProfileNotApproved = "profile-not-approved";
    public const string MissingUpstream = "missing-upstream";
    public const string SchemaIncompatible = "schema-incompatible";
    public const string Cycle = "cycle";
    public const string UnknownCompletionPolicy = "unknown-completion-policy";
    public const string InvalidSegmentationPolicy = "invalid-segmentation-policy";
    public const string InvalidGraph = "invalid-graph";
    public const string RequestedCapabilityMissing = "requested-capability-missing";

    public const string AllRequestedCapabilities = "all-requested-capabilities";

    public static IReadOnlyList<WorkflowViolation> Validate(WorkflowInput input)
    {
        var violations = new List<WorkflowViolation>();

        if (input.CompletionPolicy != AllRequestedCapabilities)
        {
            violations.Add(new(UnknownCompletionPolicy, $"The completion policy '{input.CompletionPolicy}' is not known."));
        }

        if (input.SegmentationPolicy.DurationSeconds <= 0 || !Sha256Digest.TryParse(input.SegmentationPolicy.Digest, out _))
        {
            violations.Add(new(InvalidSegmentationPolicy, "The segmentation policy needs a positive duration and a SHA-256 digest."));
        }

        foreach (var node in input.Nodes)
        {
            if (node.Profile.State != ProfileState.Approved)
            {
                violations.Add(new(ProfileNotApproved, $"Node '{node.NodeKey}' references profile '{node.Profile.ProfileId}' version {node.Profile.Version}, which is {node.Profile.State.ToPersistedName()}."));
            }
        }

        var graph = new WorkflowGraph(
            input.Nodes.Select(n => new WorkflowNodeDefinition(n.NodeKey, n.Declaration.CapabilityId, n.Declaration.ExecutionScope)),
            input.Edges);
        var graphResult = graph.Validate(input.RequestedCapabilities);
        if (!graphResult.IsValid)
        {
            var code = graphResult.Reason == WorkflowGraph.Cycle ? Cycle : InvalidGraph;
            violations.Add(new(code, graphResult.Reason ?? "The graph is invalid."));
            return violations;
        }

        foreach (var requested in input.RequestedCapabilities.Distinct(StringComparer.Ordinal))
        {
            if (!input.Nodes.Any(n => string.Equals(n.Declaration.CapabilityId, requested, StringComparison.Ordinal)))
            {
                violations.Add(new(RequestedCapabilityMissing, $"The requested capability '{requested}' has no node."));
            }
        }

        ValidateUpstreams(input, violations);
        return violations;
    }

    private static void ValidateUpstreams(WorkflowInput input, List<WorkflowViolation> violations)
    {
        var nodes = input.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
        foreach (var node in input.Nodes)
        {
            var incoming = input.Edges
                .Where(e => string.Equals(e.DependentNodeKey, node.NodeKey, StringComparison.Ordinal))
                .Select(e => (Edge: e, Provider: nodes[e.UpstreamNodeKey]))
                .ToArray();

            foreach (var upstream in node.Declaration.Upstream)
            {
                var providers = incoming
                    .Where(i => string.Equals(i.Provider.Declaration.CapabilityId, upstream.CapabilityId, StringComparison.Ordinal))
                    .ToArray();

                if (upstream.Kind == DependencyKind.Required
                    && !providers.Any(p => AnalysisPersistedNames.TryParseDependencyKind(p.Edge.Kind, out var kind) && kind == DependencyKind.Required))
                {
                    violations.Add(new(MissingUpstream, $"Node '{node.NodeKey}' requires capability '{upstream.CapabilityId}' through a required edge."));
                    continue;
                }

                foreach (var (_, provider) in providers)
                {
                    if (!string.Equals(provider.Declaration.CapabilityVersion, upstream.CapabilityVersion, StringComparison.Ordinal)
                        || !string.Equals(provider.Profile.Content.OutputSchemaVersion, upstream.SchemaVersion, StringComparison.Ordinal))
                    {
                        violations.Add(new(SchemaIncompatible, $"Node '{provider.NodeKey}' does not provide capability '{upstream.CapabilityId}' {upstream.CapabilityVersion} with schema version '{upstream.SchemaVersion}' for node '{node.NodeKey}'."));
                    }
                }
            }
        }
    }
}
