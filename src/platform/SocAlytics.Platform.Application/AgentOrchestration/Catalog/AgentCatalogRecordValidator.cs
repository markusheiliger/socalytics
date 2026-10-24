using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

/// <summary>A record that passed validation, with the canonical document and digest to store.</summary>
public sealed record ValidatedCatalogRecord(
    string Kind,
    string Id,
    string? Version,
    string? Group,
    string CanonicalDocument,
    string Digest);

/// <summary>Violations are empty exactly when <see cref="Record"/> is set.</summary>
public sealed record AgentCatalogValidationResult(IReadOnlyList<AgentCatalogViolation> Violations, ValidatedCatalogRecord? Record)
{
    public bool IsValid => Violations.Count == 0 && Record is not null;
}

/// <summary>
/// Validates a catalog record in two stages: structure against its embedded contract, then the cross-record rules of
/// <see cref="AgentCatalogRules"/> with the referenced records loaded from the stores. All violations are collected.
/// </summary>
public sealed class AgentCatalogRecordValidator(IContractValidator contracts, IAgentCatalogArtifactStore artifacts)
{
    public const string AgentVersionKind = "agent-version";

    public static string SchemaIdOf(string kindName) =>
        $"https://socalytics.invalid/contracts/agent-catalog/{kindName}/v1/{kindName}.schema.json";

    public async Task<AgentCatalogValidationResult> ValidateArtifactAsync(
        AgentCatalogRecordKind kind, JsonElement document, CancellationToken cancellationToken)
    {
        var kindName = AgentCatalogRecordKinds.ToName(kind);
        var structural = Structure(kindName, document);
        if (structural.Count > 0)
        {
            return new(structural, null);
        }

        var id = document.GetProperty("id").GetString()!;
        var version = document.GetProperty("version").GetString()!;
        var violations = new List<AgentCatalogViolation>();
        string? group = null;

        switch (kind)
        {
            case AgentCatalogRecordKind.PromptTemplate:
                var prompt = ParsePrompt(document);
                group = prompt.Group;
                violations.AddRange(AgentCatalogRules.ValidatePromptTemplate(prompt));
                break;
            case AgentCatalogRecordKind.ModelSelection:
                violations.AddRange(AgentCatalogRules.ValidateModelSelection(new(Str(document, "provider"))));
                break;
            case AgentCatalogRecordKind.ToolPolicy:
                violations.AddRange(AgentCatalogRules.ValidateToolPolicy(new(Strings(document, "tools"))));
                break;
            case AgentCatalogRecordKind.SafetyPolicy:
                var safety = new SafetyPolicyContent(Str(document, "group"), Strings(document, "rules"));
                group = safety.Group;
                violations.AddRange(AgentCatalogRules.ValidateSafetyPolicy(safety));
                break;
            case AgentCatalogRecordKind.DimensionSet:
                var set = ParseDimensionSet(document);
                var sameMajor = await artifacts.ListByMajorAsync(kind, id, SemanticVersion.MajorOf(version) ?? 0, cancellationToken);
                var existing = sameMajor
                    .Where(r => !string.Equals(r.Version, version, StringComparison.Ordinal))
                    .Select(r => ParseDimensionSet(Parse(r.Document)))
                    .ToList();
                violations.AddRange(AgentCatalogRules.ValidateDimensionSet(set, existing));
                break;
            case AgentCatalogRecordKind.PhilosophyProfile:
                var profile = ParseProfile(document);
                var dimensionSet = await ResolveAsync(
                    AgentCatalogRecordKind.DimensionSet, profile.DimensionSetId, profile.DimensionSetVersion, null, ParseDimensionSet, cancellationToken);
                var versions = (await artifacts.ListAsync(kind, id, cancellationToken)).Select(r => r.Version).ToList();
                violations.AddRange(AgentCatalogRules.ValidateProfile(profile, dimensionSet, versions));
                break;
            case AgentCatalogRecordKind.ModifierPolicy:
                violations.AddRange(AgentCatalogRules.ValidateModifierPolicy(ParseModifier(document)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return Result(violations, kindName, id, version, group, document);
    }

    public async Task<AgentCatalogValidationResult> ValidateAgentVersionAsync(JsonElement document, CancellationToken cancellationToken)
    {
        var structural = Structure(AgentVersionKind, document);
        if (structural.Count > 0)
        {
            return new(structural, null);
        }

        var content = ParseAgentVersion(document);
        var prompt = await ResolveAsync(AgentCatalogRecordKind.PromptTemplate, content.PromptTemplate, ParsePrompt, cancellationToken);
        var model = await ResolveAsync(AgentCatalogRecordKind.ModelSelection, content.ModelSelection, e => new ModelSelectionContent(Str(e, "provider")), cancellationToken);
        var tools = await ResolveAsync(AgentCatalogRecordKind.ToolPolicy, content.ToolPolicy, e => new ToolPolicyContent(Strings(e, "tools")), cancellationToken);
        var safety = await ResolveAsync(
            AgentCatalogRecordKind.SafetyPolicy, content.SafetyPolicy, e => new SafetyPolicyContent(Str(e, "group"), Strings(e, "rules")), cancellationToken);

        ResolvedArtifact<PhilosophyProfileContent>? profile = null;
        ResolvedArtifact<DimensionSetContent>? dimensionSet = null;
        ResolvedArtifact<ModifierPolicyContent>? modifier = null;
        if (content.PhilosophyProfile is not null)
        {
            profile = await ResolveAsync(AgentCatalogRecordKind.PhilosophyProfile, content.PhilosophyProfile, ParseProfile, cancellationToken);
            if (profile.Record is not null)
            {
                dimensionSet = await ResolveAsync(
                    AgentCatalogRecordKind.DimensionSet, profile.Record.DimensionSetId, profile.Record.DimensionSetVersion, null, ParseDimensionSet, cancellationToken);
            }
        }

        if (content.ModifierPolicy is not null)
        {
            modifier = await ResolveAsync(AgentCatalogRecordKind.ModifierPolicy, content.ModifierPolicy, ParseModifier, cancellationToken);
        }

        var violations = AgentCatalogRules.ValidateAgentVersion(
            content, new AgentVersionReferences(prompt, model, tools, safety, profile, dimensionSet, modifier));
        return Result(violations, AgentVersionKind, content.AgentId, null, content.Group, document);
    }

    private List<AgentCatalogViolation> Structure(string kindName, JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return [new(AgentCatalogViolationCodes.SchemaViolation, string.Empty, "The record is a JSON object.")];
        }

        if (!document.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !string.Equals(kind.GetString(), kindName, StringComparison.Ordinal))
        {
            return [new(AgentCatalogViolationCodes.SchemaViolation, "/kind", $"The record kind is '{kindName}'.")];
        }

        var result = contracts.Validate(SchemaIdOf(kindName), document);
        return result.IsValid
            ? []
            : [.. result.Errors.Select(e => new AgentCatalogViolation(AgentCatalogViolationCodes.SchemaViolation, e.Field, e.Keyword))];
    }

    // The digest covers the canonical document, so equal content always yields an equal digest.
    private static AgentCatalogValidationResult Result(
        IReadOnlyList<AgentCatalogViolation> violations, string kind, string id, string? version, string? group, JsonElement document)
    {
        if (violations.Count > 0)
        {
            return new(violations, null);
        }

        var canonical = CanonicalJson.Canonicalize(document);
        return new([], new ValidatedCatalogRecord(kind, id, version, group, canonical, ContentDigest.OfCanonical(canonical).ToString()));
    }

    private Task<ResolvedArtifact<T>> ResolveAsync<T>(
        AgentCatalogRecordKind kind, ArtifactReference reference, Func<JsonElement, T> parse, CancellationToken cancellationToken)
        where T : class =>
        ResolveAsync(kind, reference.Id, reference.Version, reference.Digest, parse, cancellationToken);

    // A null digest accepts any digest of the identity.
    private async Task<ResolvedArtifact<T>> ResolveAsync<T>(
        AgentCatalogRecordKind kind, string id, string version, string? digest, Func<JsonElement, T> parse, CancellationToken cancellationToken)
        where T : class
    {
        var record = await artifacts.FindAsync(kind, id, version, cancellationToken);
        if (record is not null)
        {
            return digest is null || string.Equals(record.Digest, digest, StringComparison.Ordinal)
                ? new(parse(Parse(record.Document)))
                : new(null);
        }

        var other = await artifacts.FindAnyKindAsync(id, version, cancellationToken);
        return new(null, other is not null && other.Kind != kind);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static List<string> Strings(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

    private static PromptTemplateContent ParsePrompt(JsonElement e) =>
        new(Str(e, "id"), Str(e, "version"), Str(e, "group"), Str(e, "text"), Strings(e, "slots"),
            e.TryGetProperty("dimensionSetMajor", out var major) && major.TryGetInt32(out var value) ? value : null);

    private static DimensionSetContent ParseDimensionSet(JsonElement e) =>
        new(Str(e, "id"), Str(e, "version"),
            [.. e.GetProperty("dimensions").EnumerateArray().Select(d => new DimensionContent(Str(d, "id"), Strings(d, "acceptedEvidence")))]);

    private static PhilosophyProfileContent ParseProfile(JsonElement e)
    {
        var weights = new Dictionary<string, ProfileWeightContent>(StringComparer.Ordinal);
        foreach (var property in e.GetProperty("weights").EnumerateObject())
        {
            var w = property.Value;
            weights[property.Name] = new ProfileWeightContent(
                w.GetProperty("weight").TryGetInt32(out var weight) ? weight : -1, Str(w, "rationale"), Str(w, "provenance"));
        }

        var set = e.GetProperty("dimensionSet");
        var supersedes = e.TryGetProperty("supersedes", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "version") : null;
        return new(Str(e, "id"), Str(e, "version"), Str(set, "id"), Str(set, "version"), weights, supersedes);
    }

    private static ModifierPolicyContent ParseModifier(JsonElement e) =>
        new(e.GetProperty("dimensionSetMajor").GetInt32(), e.GetProperty("perDimensionBound").GetInt32(),
            e.GetProperty("maxPositiveMovement").GetInt32(), Strings(e, "contextCategories"));

    private static AgentVersionContent ParseAgentVersion(JsonElement e)
    {
        var references = e.GetProperty("artifacts");
        ArtifactReference? Optional(string name) => references.TryGetProperty(name, out var r) ? Reference(r) : null;
        return new(
            Str(e, "agentId"), Str(e, "group"),
            e.TryGetProperty("disclosure", out var disclosure) ? disclosure.GetString() : null,
            Reference(references.GetProperty("promptTemplate")), Reference(references.GetProperty("modelSelection")),
            Reference(references.GetProperty("toolPolicy")), Reference(references.GetProperty("safetyPolicy")),
            Optional("philosophyProfile"), Optional("modifierPolicy"));
    }

    private static ArtifactReference Reference(JsonElement e) => new(Str(e, "id"), Str(e, "version"), Str(e, "digest"));
}
