namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

public sealed record AgentCatalogViolation(string Code, string Pointer, string Detail);

public static class AgentCatalogViolationCodes
{
	public const string SchemaViolation = "schema-violation";
	public const string UnknownTool = "unknown-tool";
	public const string ToolOutsideBaseline = "tool-outside-baseline";
	public const string UnknownAgentId = "unknown-agent-id";
	public const string GroupMismatch = "group-mismatch";
	public const string ArtifactNotFound = "artifact-not-found";
	public const string ArtifactKindMismatch = "artifact-kind-mismatch";
	public const string MissingDimension = "missing-dimension";
	public const string UnknownDimension = "unknown-dimension";
	public const string WeightOutOfRange = "weight-out-of-range";
	public const string WeightsTotalNot100 = "weights-total-not-100";
	public const string RationaleMissing = "rationale-missing";
	public const string DimensionSetNotFound = "dimension-set-not-found";
	public const string DimensionSetIncompatible = "dimension-set-incompatible";
	public const string DimensionSetIdsChanged = "dimension-set-ids-changed";
	public const string ModifierBoundExceeded = "modifier-bound-exceeded";
	public const string ModifierMovementExceeded = "modifier-movement-exceeded";
	public const string ContextCategoryNotPermitted = "context-category-not-permitted";
	public const string ProfileSlotMissing = "profile-slot-missing";
	public const string ProfileSlotNotAllowed = "profile-slot-not-allowed";
	public const string DisclosureMissing = "disclosure-missing";
	public const string SafetyRuleMissing = "safety-rule-missing";
	public const string SupersededProfileInvalid = "superseded-profile-invalid";
	public const string ProviderNotPermitted = "provider-not-permitted";
}
