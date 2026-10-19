using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Registry;

public static class WorkflowCompletionPolicies
{
    public const string AllRequestedCapabilities = "all-requested-capabilities";
}

public sealed record SegmentationPolicy(int Version, int DurationSeconds, Sha256Digest Digest);

public sealed record ResolvedWorkflowNode(
    string NodeKey,
    string CapabilityId,
    string CapabilityVersion,
    CapabilityTier Tier,
    ExecutionScope ExecutionScope,
    string SchemaVersion,
    string AnalystProfileId,
    Sha256Digest AnalystProfileDigest,
    Sha256Digest ImageDigest,
    Sha256Digest? ModelDigest,
    IReadOnlyDictionary<string, string> PolicyVersions,
    IReadOnlyDictionary<string, string> Resources,
    int MaxAttempts,
    int LeaseSeconds,
    int StaleTimeoutSeconds,
    int ExecutionTimeoutSeconds);

public sealed record ResolvedWorkflowEdge(
    string UpstreamNodeKey,
    string DependentNodeKey,
    string Kind,
    ConditionalPredicate? Predicate = null)
{
    public CoveragePolicy CoveragePolicy { get; init; } = CoveragePolicy.Complete;
}

public sealed record ResolvedWorkflowDefinition(
    string WorkflowId,
    string WorkflowVersion,
    IReadOnlyList<string> RequestedCapabilities,
    string CompletionPolicy,
    SegmentationPolicy SegmentationPolicy,
    IReadOnlyList<ResolvedWorkflowNode> Nodes,
    IReadOnlyList<ResolvedWorkflowEdge> Edges);

public abstract record WorkflowResolution
{
    public const string NotConfigured = "not-configured";
    public const string InvalidDefinition = "invalid-definition";
    public const string RegistryUnavailable = "registry-unavailable";

    private WorkflowResolution()
    {
    }

    public static WorkflowResolution Resolved(ResolvedWorkflowDefinition definition) => new ResolvedResult(definition);

    public static WorkflowResolution Unavailable(string category) => new UnavailableResult(category);

    public sealed record ResolvedResult(ResolvedWorkflowDefinition Definition) : WorkflowResolution;

    public sealed record UnavailableResult(string Category) : WorkflowResolution;
}
