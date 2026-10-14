using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Infrastructure.Registry;

/// <summary>Validates manifests with the contract validator against the analyst manifest schema (identical to its 1.0.0 release).</summary>
internal sealed class AnalystManifestValidator(IContractValidator contracts) : IAnalystManifestValidator
{
    public const string SchemaId = "https://socalytics.invalid/contracts/analysis/analyst-manifest/v1/analyst-manifest.schema.json";

    public AnalystManifestValidationResult Validate(string imageDigest, string manifestJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return Invalid(new ContractValidationError(string.Empty, "json"));
        }

        using (document)
        {
            var result = contracts.Validate(SchemaId, document.RootElement);
            if (!result.IsValid)
            {
                return new AnalystManifestValidationResult(false, result.Errors, null);
            }
        }

        var root = JsonNode.Parse(manifestJson)!.AsObject();
        var capability = root["capability"]!.AsObject();
        if (!AnalysisPersistedNames.TryParseCapabilityTier(capability["tier"]!.GetValue<string>(), out var tier)
            || !AnalysisPersistedNames.TryParseExecutionScope(capability["executionScope"]!.GetValue<string>(), out var scope))
        {
            return Invalid(new ContractValidationError("/capability", "enum"));
        }

        var runtime = root["runtime"]!.AsObject();
        var requirements = root["requirements"]!.AsObject();
        var upstream = new List<DeclaredUpstream>();
        foreach (var item in root["upstream"]!.AsArray())
        {
            if (!AnalysisPersistedNames.TryParseDependencyKind(item!["kind"]!.GetValue<string>(), out var kind))
            {
                return Invalid(new ContractValidationError("/upstream", "enum"));
            }

            upstream.Add(new DeclaredUpstream(
                item["capabilityId"]!.GetValue<string>(), item["capabilityVersion"]!.GetValue<string>(),
                item["schemaVersion"]!.GetValue<string>(), kind));
        }

        // The capability must not list itself or the same upstream capability twice.
        var capabilityId = capability["id"]!.GetValue<string>();
        if (upstream.Any(u => u.CapabilityId == capabilityId))
        {
            return Invalid(new ContractValidationError("/upstream", "self-reference"));
        }

        if (upstream.GroupBy(u => u.CapabilityId, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            return Invalid(new ContractValidationError("/upstream", "duplicate-capability"));
        }

        var declaration = new CapabilityDeclaration(
            imageDigest,
            capabilityId,
            capability["version"]!.GetValue<string>(),
            tier,
            scope,
            root["model"]!["digest"]!.GetValue<string>(),
            root["videoAccess"]!.GetValue<string>(),
            root["output"]!["schemaVersion"]!.GetValue<string>(),
            Strings(root["compatibleProfiles"]),
            Strings(runtime["constraints"]),
            Strings(runtime["accelerators"]),
            root["resources"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal),
            upstream,
            new DeclaredRequirements(
                requirements["minMaxAttempts"]!.GetValue<int>(),
                requirements["minLeaseSeconds"]!.GetValue<int>(),
                requirements["minStaleTimeoutSeconds"]!.GetValue<int>(),
                requirements["minExecutionTimeoutSeconds"]!.GetValue<int>()));
        return new AnalystManifestValidationResult(true, [], declaration);
    }

    private static AnalystManifestValidationResult Invalid(ContractValidationError error) => new(false, [error], null);

    private static List<string> Strings(JsonNode? node) =>
        node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}
