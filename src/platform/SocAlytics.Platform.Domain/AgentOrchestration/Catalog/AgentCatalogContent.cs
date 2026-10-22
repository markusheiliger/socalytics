namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

// The validated content of catalog records as the rules see them; parsing from JSON belongs to the Application layer.
public sealed record ArtifactReference(string Id, string Version, string Digest);

public sealed record PromptTemplateContent(string Id, string Version, string Group, string Text, IReadOnlyList<string> Slots, int? DimensionSetMajor);

public sealed record ModelSelectionContent(string Provider);

public sealed record ToolPolicyContent(IReadOnlyList<string> Tools);

public sealed record SafetyPolicyContent(string Group, IReadOnlyList<string> Rules);

public sealed record DimensionContent(string Id, IReadOnlyList<string> AcceptedEvidence);

public sealed record DimensionSetContent(string Id, string Version, IReadOnlyList<DimensionContent> Dimensions);

public sealed record ProfileWeightContent(int Weight, string Rationale, string Provenance);

public sealed record PhilosophyProfileContent(
	string Id,
	string Version,
	string DimensionSetId,
	string DimensionSetVersion,
	IReadOnlyDictionary<string, ProfileWeightContent> Weights,
	string? SupersedesVersion);

public sealed record ModifierPolicyContent(
	int DimensionSetMajor,
	int PerDimensionBound,
	int MaxPositiveMovement,
	IReadOnlyList<string> ContextCategories);

// A referenced artifact as loaded from the store: null Record means no record with that identity and digest exists;
// KindMismatch means the identity exists under another kind.
public sealed record ResolvedArtifact<T>(T? Record, bool KindMismatch = false)
	where T : class;

public sealed record AgentVersionContent(
	string AgentId,
	string Group,
	string? Disclosure,
	ArtifactReference PromptTemplate,
	ArtifactReference ModelSelection,
	ArtifactReference ToolPolicy,
	ArtifactReference SafetyPolicy,
	ArtifactReference? PhilosophyProfile,
	ArtifactReference? ModifierPolicy);

public sealed record AgentVersionReferences(
	ResolvedArtifact<PromptTemplateContent> PromptTemplate,
	ResolvedArtifact<ModelSelectionContent> ModelSelection,
	ResolvedArtifact<ToolPolicyContent> ToolPolicy,
	ResolvedArtifact<SafetyPolicyContent> SafetyPolicy,
	ResolvedArtifact<PhilosophyProfileContent>? PhilosophyProfile,
	ResolvedArtifact<DimensionSetContent>? DimensionSet,
	ResolvedArtifact<ModifierPolicyContent>? ModifierPolicy);
