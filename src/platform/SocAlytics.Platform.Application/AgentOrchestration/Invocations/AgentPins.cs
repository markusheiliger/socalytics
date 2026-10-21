using System.Text.Json;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

internal static class AgentPins
{
    private static readonly (string Property, AgentCatalogRecordKind Kind)[] ReferencedKinds =
    [
        ("promptTemplate", AgentCatalogRecordKind.PromptTemplate),
        ("modelSelection", AgentCatalogRecordKind.ModelSelection),
        ("toolPolicy", AgentCatalogRecordKind.ToolPolicy),
        ("safetyPolicy", AgentCatalogRecordKind.SafetyPolicy),
        ("philosophyProfile", AgentCatalogRecordKind.PhilosophyProfile),
        ("modifierPolicy", AgentCatalogRecordKind.ModifierPolicy),
    ];

    /// <summary>Reads the pins of an invocation; null for an invocation submitted before pinning existed.</summary>
    public static async Task<AgentPinView?> ReadAsync(
        IAgentInvocationPinStore pins, AgentInvocationRecord invocation, CancellationToken cancellationToken)
    {
        if (invocation.AgentVersion is null || invocation.AgentVersionDigest is null)
        {
            return null;
        }

        return new AgentPinView(
            invocation.AgentVersion, invocation.AgentVersionDigest, await pins.ListAsync(invocation.Id, cancellationToken));
    }

    /// <summary>Resolves the artifacts of an agent version document, adding the profile's dimension set for a Coach Agent.</summary>
    public static async Task<IReadOnlyList<InvocationPin>> ResolveAsync(
        IAgentCatalogArtifactStore artifacts, string agentVersionDocument, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(agentVersionDocument);
        var references = document.RootElement.GetProperty("artifacts");
        var result = new List<InvocationPin>();
        foreach (var (property, kind) in ReferencedKinds)
        {
            if (!references.TryGetProperty(property, out var reference))
            {
                continue;
            }

            var pin = new InvocationPin(
                kind, reference.GetProperty("id").GetString()!, reference.GetProperty("version").GetString()!,
                reference.GetProperty("digest").GetString()!);
            result.Add(pin);

            if (kind == AgentCatalogRecordKind.PhilosophyProfile)
            {
                var profile = await artifacts.FindAsync(kind, pin.ArtifactId, pin.ArtifactVersion, cancellationToken)
                    ?? throw new InvalidOperationException($"The pinned profile '{pin.ArtifactId}' {pin.ArtifactVersion} is missing.");
                using var profileDocument = JsonDocument.Parse(profile.Document);
                var set = profileDocument.RootElement.GetProperty("dimensionSet");
                var setId = set.GetProperty("id").GetString()!;
                var setVersion = set.GetProperty("version").GetString()!;
                var setRecord = await artifacts.FindAsync(AgentCatalogRecordKind.DimensionSet, setId, setVersion, cancellationToken)
                    ?? throw new InvalidOperationException($"The dimension set '{setId}' {setVersion} is missing.");
                result.Add(new InvocationPin(AgentCatalogRecordKind.DimensionSet, setId, setVersion, setRecord.Digest));
            }
        }

        return result;
    }
}
