using System.Text.Json;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Domain.Registry;

public static class VideoPolicies
{
    public const string Required = "required";
    public const string Optional = "optional";
    public const string Forbidden = "forbidden";

    public static bool IsKnown(string? value) => value is Required or Optional or Forbidden;
}

public sealed record DeclaredUpstream(string CapabilityId, string CapabilityVersion, string SchemaVersion, DependencyKind Kind)
{
    public CoveragePolicy CoveragePolicy { get; init; } = CoveragePolicy.Complete;
}

public sealed record DeclaredRequirements(
    int MinMaxAttempts,
    int MinLeaseSeconds,
    int MinStaleTimeoutSeconds,
    int MinExecutionTimeoutSeconds);

/// <summary>The immutable content of an imported Analyst manifest, keyed by image digest.</summary>
public sealed record CapabilityDeclaration(
    string ImageDigest,
    string CapabilityId,
    string CapabilityVersion,
    CapabilityTier Tier,
    ExecutionScope ExecutionScope,
    string ModelDigest,
    string VideoAccess,
    string OutputSchemaVersion,
    IReadOnlyList<string> CompatibleProfiles,
    IReadOnlyList<string> RuntimeConstraints,
    IReadOnlyList<string> Accelerators,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyList<DeclaredUpstream> Upstream,
    DeclaredRequirements Requirements)
{
    public IReadOnlyDictionary<string, JsonElement> FactPayloadSchemas { get; init; } =
        new Dictionary<string, JsonElement>();
}
