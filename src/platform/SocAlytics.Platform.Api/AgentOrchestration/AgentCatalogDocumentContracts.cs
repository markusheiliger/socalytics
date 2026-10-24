using System.Text.Json.Serialization;

namespace SocAlytics.Platform.Api.AgentOrchestration;

// Schema-only descriptions of the documents in contracts/agent-catalog/. They are attached to the endpoints as OpenAPI
// metadata only: request bodies are read as raw JSON and validated through IContractValidator, and responses write the
// stored canonical document unchanged.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(DimensionSetDocument), "dimension-set")]
[JsonDerivedType(typeof(PhilosophyProfileDocument), "philosophy-profile")]
[JsonDerivedType(typeof(PromptTemplateDocument), "prompt-template")]
[JsonDerivedType(typeof(ToolPolicyDocument), "tool-policy")]
[JsonDerivedType(typeof(SafetyPolicyDocument), "safety-policy")]
[JsonDerivedType(typeof(ModelSelectionDocument), "model-selection")]
[JsonDerivedType(typeof(ModifierPolicyDocument), "modifier-policy")]
internal abstract record AgentCatalogRecordDocument;

internal sealed record DimensionDocument(string Id, string Meaning, IReadOnlyList<string> AcceptedEvidence);

internal sealed record DimensionSetDocument(
	string SchemaVersion, string Id, string Version, IReadOnlyList<DimensionDocument> Dimensions) : AgentCatalogRecordDocument;

internal sealed record DimensionSetReferenceDocument(string Id, string Version);

internal sealed record ProfileWeightDocument(int Weight, string Rationale, string Provenance);

internal sealed record ProfileReviewersDocument(IReadOnlyList<string> Architecture, IReadOnlyList<string> FootballDomain);

internal sealed record ProfileSupersedesDocument(string Version);

internal sealed record PhilosophyProfileDocument(
	string SchemaVersion, string Id, string Version, DimensionSetReferenceDocument DimensionSet,
	IReadOnlyDictionary<string, ProfileWeightDocument> Weights, string Author, ProfileReviewersDocument Reviewers,
	ProfileSupersedesDocument? Supersedes = null) : AgentCatalogRecordDocument;

internal sealed record PromptTemplateDocument(
	string SchemaVersion, string Id, string Version, string Group, string ResponseContract, string Text,
	IReadOnlyList<string> Slots, int? DimensionSetMajor = null) : AgentCatalogRecordDocument;

internal sealed record ToolPolicyDocument(
	string SchemaVersion, string Id, string Version, IReadOnlyList<string> Tools) : AgentCatalogRecordDocument;

internal sealed record SafetyPolicyDocument(
	string SchemaVersion, string Id, string Version, string Group, IReadOnlyList<string> Routes,
	IReadOnlyList<string> HardBoundaries, IReadOnlyList<string> EscalationCategories,
	IReadOnlyList<string> RefusalCategories, IReadOnlyList<string> Rules) : AgentCatalogRecordDocument;

internal sealed record ModelParametersDocument(
	double? Temperature = null, double? TopP = null, int? MaxOutputTokens = null, string? ReasoningEffort = null);

internal sealed record ModelSelectionDocument(
	string SchemaVersion, string Id, string Version, string Provider, string Model, string Deployment,
	ModelParametersDocument? Parameters = null) : AgentCatalogRecordDocument;

internal sealed record ModifierPolicyDocument(
	string SchemaVersion, string Id, string Version, int DimensionSetMajor, int PerDimensionBound,
	int MaxPositiveMovement, IReadOnlyList<string> ContextCategories, bool BudgetNeutral) : AgentCatalogRecordDocument;

internal sealed record ArtifactReferenceDocument(string Id, string Version, string Digest);

internal sealed record AgentVersionArtifactsDocument(
	ArtifactReferenceDocument PromptTemplate, ArtifactReferenceDocument ModelSelection, ArtifactReferenceDocument ToolPolicy,
	ArtifactReferenceDocument SafetyPolicy, ArtifactReferenceDocument? PhilosophyProfile = null,
	ArtifactReferenceDocument? ModifierPolicy = null);

internal sealed record AgentVersionDocument(
	string SchemaVersion, string Kind, string AgentId, string Group, string DisplayName,
	AgentVersionArtifactsDocument Artifacts, string? Disclosure = null);

// Problem bodies whose extension members the snapshot would otherwise not describe.
internal sealed record AgentCatalogValidationProblemDto(
	string Type, string Title, int Status, string Code, string CorrelationId, IReadOnlyList<AgentCatalogViolationDto> Violations);

internal sealed record AgentCatalogRefusalProblemDto(
	string Type, string Title, int Status, string Code, string CorrelationId, string? Reason = null);

internal static class AgentCatalogDocumentSchemas
{
	// Documents travel as raw JsonElement; the snapshot describes them through the schema-only records above.
	public static async Task TransformAsync(
		Microsoft.OpenApi.OpenApiSchema schema, Microsoft.AspNetCore.OpenApi.OpenApiSchemaTransformerContext context,
		CancellationToken cancellationToken)
	{
		var documentType = context.JsonTypeInfo.Type == typeof(AgentCatalogArtifactDto) ? typeof(AgentCatalogRecordDocument)
			: context.JsonTypeInfo.Type == typeof(AgentVersionDto) ? typeof(AgentVersionDocument)
			: null;
		if (documentType is not null && schema.Properties is { } properties && properties.ContainsKey("document"))
		{
			properties["document"] = await context.GetOrCreateSchemaAsync(documentType, null, cancellationToken);
		}
	}
}
