using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Infrastructure.Registry;

/// <summary>Resolves the default workflow from the Registry in one read transaction, in its own DI scope so callers inside a unit of work are unaffected.</summary>
internal sealed class RegistryWorkflowDefinitionResolver(IServiceScopeFactory scopes) : IWorkflowDefinitionResolver
{
    /// <summary>Marks a declaration without a model (a manifest always carries a model digest); it resolves to no pinned model.</summary>
    public static readonly string NoModelDigest = Sha256Digest.Prefix + new string('0', 64);

    public async Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            await using var unit = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken);
            return await ResolveAsync(
                services.GetRequiredService<IWorkflowDefinitionStore>(),
                services.GetRequiredService<IAnalystProfileStore>(),
                services.GetRequiredService<ICapabilityDeclarationStore>(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return WorkflowResolution.Unavailable(WorkflowResolution.RegistryUnavailable);
        }
    }

    private static async Task<WorkflowResolution> ResolveAsync(
        IWorkflowDefinitionStore workflows,
        IAnalystProfileStore profiles,
        ICapabilityDeclarationStore declarations,
        CancellationToken cancellationToken)
    {
        var current = await workflows.GetDefaultAsync(cancellationToken);
        if (current is null)
        {
            return WorkflowResolution.Unavailable(WorkflowResolution.NotConfigured);
        }

        var stored = await workflows.FindAsync(current.WorkflowId, current.WorkflowVersion, cancellationToken);
        if (stored is null)
        {
            return WorkflowResolution.Unavailable(WorkflowResolution.InvalidDefinition);
        }

        try
        {
            var definition = await MapAsync(stored, profiles, declarations, cancellationToken);
            return definition is null
                ? WorkflowResolution.Unavailable(WorkflowResolution.InvalidDefinition)
                : WorkflowResolution.Resolved(definition);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return WorkflowResolution.Unavailable(WorkflowResolution.InvalidDefinition);
        }
    }

    private static async Task<ResolvedWorkflowDefinition?> MapAsync(
        StoredWorkflowDefinition stored,
        IAnalystProfileStore profiles,
        ICapabilityDeclarationStore declarations,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(stored.DefinitionJson);
        var root = document.RootElement;
        var segmentation = root.GetProperty("segmentationPolicy");
        if (!Sha256Digest.TryParse(segmentation.GetProperty("digest").GetString(), out var segmentationDigest))
        {
            return null;
        }

        var nodes = new List<ResolvedWorkflowNode>();
        var declaredUpstream = new Dictionary<string, IReadOnlyList<DeclaredUpstream>>(StringComparer.Ordinal);
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            var mapped = await MapNodeAsync(node, profiles, declarations, cancellationToken);
            if (mapped is null)
            {
                return null;
            }

            nodes.Add(mapped.Value.Node);
            declaredUpstream[mapped.Value.Node.NodeKey] = mapped.Value.Upstream;
        }

        var edges = new List<ResolvedWorkflowEdge>();
        foreach (var edge in root.GetProperty("edges").EnumerateArray())
        {
            ConditionalPredicate? predicate = null;
            if (edge.TryGetProperty("predicate", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                predicate = new ConditionalPredicate(
                    p.GetProperty("kind").GetString()!,
                    p.TryGetProperty("capabilityId", out var capability) ? capability.GetString() : null,
                    p.TryGetProperty("nodeKey", out var nodeKey) ? nodeKey.GetString() : null);
            }

            var upstreamKey = edge.GetProperty("upstreamNodeKey").GetString()!;
            var dependentKey = edge.GetProperty("dependentNodeKey").GetString()!;
            var upstreamNode = nodes.FirstOrDefault(n => n.NodeKey == upstreamKey);
            var coverage = upstreamNode is not null && declaredUpstream.TryGetValue(dependentKey, out var declared)
                ? declared.FirstOrDefault(d => d.CapabilityId == upstreamNode.CapabilityId)?.CoveragePolicy ?? CoveragePolicy.Complete
                : CoveragePolicy.Complete;

            edges.Add(new ResolvedWorkflowEdge(
                upstreamKey,
                dependentKey,
                edge.GetProperty("kind").GetString()!,
                predicate)
            {
                CoveragePolicy = coverage,
            });
        }

        return new ResolvedWorkflowDefinition(
            stored.WorkflowId,
            stored.WorkflowVersion,
            root.GetProperty("requestedCapabilities").EnumerateArray().Select(c => c.GetString()!).ToArray(),
            root.GetProperty("completionPolicy").GetString()!,
            new SegmentationPolicy(segmentation.GetProperty("version").GetInt32(), segmentation.GetProperty("durationSeconds").GetInt32(), segmentationDigest),
            nodes,
            edges);
    }

    private static async Task<(ResolvedWorkflowNode Node, IReadOnlyList<DeclaredUpstream> Upstream)?> MapNodeAsync(
        JsonElement node,
        IAnalystProfileStore profiles,
        ICapabilityDeclarationStore declarations,
        CancellationToken cancellationToken)
    {
        var stored = await profiles.FindByDigestAsync(node.GetProperty("profileDigest").GetString()!, cancellationToken);
        if (stored is null || stored.Profile.State != ProfileState.Approved)
        {
            return null;
        }

        var profile = stored.Profile;
        var content = profile.Content;
        var declaration = await declarations.FindAsync(content.ImageDigest, cancellationToken);
        if (declaration is null
            || !Sha256Digest.TryParse(ToDigestForm(content.ImageDigest), out var imageDigest))
        {
            return null;
        }

        Sha256Digest? modelDigest = null;
        if (content.ModelDigest != NoModelDigest && !Sha256Digest.TryParse(content.ModelDigest, out modelDigest))
        {
            return null;
        }

        var resolvedNode = new ResolvedWorkflowNode(
            node.GetProperty("nodeKey").GetString()!,
            content.CapabilityId,
            content.CapabilityVersion,
            declaration.Declaration.Tier,
            declaration.Declaration.ExecutionScope,
            content.OutputSchemaVersion,
            profile.ProfileId,
            profile.Digest,
            imageDigest,
            modelDigest,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(content.Resources, StringComparer.Ordinal),
            content.MaxAttempts,
            content.LeaseSeconds,
            content.StaleTimeoutSeconds,
            content.ExecutionTimeoutSeconds);
        return (resolvedNode, declaration.Declaration.Upstream);
    }

    /// <summary>The Registry stores image digests in the OCI form; the analysis workflow uses the platform digest form.</summary>
    internal static string ToDigestForm(string imageDigest) =>
        imageDigest.StartsWith("sha256:", StringComparison.Ordinal) ? Sha256Digest.Prefix + imageDigest["sha256:".Length..] : imageDigest;
}
