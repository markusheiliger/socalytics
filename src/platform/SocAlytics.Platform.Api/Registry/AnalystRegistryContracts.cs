using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record RegisterAnalystImageRequest(string? Reference);

internal sealed record AnalystProfileContentRequest(
	string? CapabilityId,
	string? CapabilityVersion,
	string? ModelDigest,
	string[]? RuntimeConstraints,
	string[]? Accelerators,
	Dictionary<string, string>? Resources,
	int MaxAttempts,
	int LeaseSeconds,
	int StaleTimeoutSeconds,
	int ExecutionTimeoutSeconds,
	string? VideoPolicy,
	Dictionary<string, string>? InputSchemaVersions,
	string? OutputSchemaVersion);

internal sealed record CreateAnalystProfileRequest(string? ProfileId, string? ImageDigest, AnalystProfileContentRequest? Content);

internal sealed record WorkflowNodeRequest(string? NodeKey, string? ProfileId, int ProfileVersion);

internal sealed record WorkflowPredicateRequest(string? Kind, string? CapabilityId, string? NodeKey);

internal sealed record WorkflowEdgeRequest(string? UpstreamNodeKey, string? DependentNodeKey, string? Kind, WorkflowPredicateRequest? Predicate);

internal sealed record WorkflowSegmentationRequest(int Version, int DurationSeconds, string? Digest);

internal sealed record DefineWorkflowRequest(
	string? WorkflowId,
	string? WorkflowVersion,
	string[]? RequestedCapabilities,
	string? CompletionPolicy,
	WorkflowSegmentationRequest? SegmentationPolicy,
	WorkflowNodeRequest[]? Nodes,
	WorkflowEdgeRequest[]? Edges);

internal sealed record SetDefaultWorkflowRequest(string? WorkflowId, string? WorkflowVersion);

internal sealed record AnalystImageCapabilityDto(string Id, string Version, string Tier, string ExecutionScope);

internal sealed record AnalystImageDto(
	string ImageDigest,
	string Repository,
	string ManifestDigest,
	AnalystImageCapabilityDto Capability,
	string ModelDigest,
	JsonNode? Manifest,
	string Source,
	DateTimeOffset RegisteredAt,
	Guid? RegisteredBy);

internal sealed record AnalystImageListItemDto(
	string ImageDigest,
	string CapabilityId,
	string CapabilityVersion,
	DateTimeOffset RegisteredAt);

internal sealed record AnalystImageListDto(AnalystImageListItemDto[] Items);

internal sealed record AnalystProfileContentDto(
	string CapabilityId,
	string CapabilityVersion,
	string ModelDigest,
	IReadOnlyList<string> RuntimeConstraints,
	IReadOnlyList<string> Accelerators,
	IReadOnlyDictionary<string, string> Resources,
	int MaxAttempts,
	int LeaseSeconds,
	int StaleTimeoutSeconds,
	int ExecutionTimeoutSeconds,
	string VideoPolicy,
	IReadOnlyDictionary<string, string> InputSchemaVersions,
	string OutputSchemaVersion);

internal sealed record AnalystProfileDto(
	string ProfileId,
	int Version,
	string ProfileDigest,
	string ImageDigest,
	string State,
	AnalystProfileContentDto Content,
	DateTimeOffset CreatedAt,
	Guid CreatedBy,
	DateTimeOffset? ApprovedAt,
	Guid? ApprovedBy,
	DateTimeOffset? RetiredAt,
	Guid? RetiredBy);

internal sealed record AnalystProfileListDto(AnalystProfileDto[] Items);

internal sealed record WorkflowNodeDto(string NodeKey, string ProfileId, int ProfileVersion, string ProfileDigest);

internal sealed record WorkflowPredicateDto(
	string Kind,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CapabilityId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NodeKey);

internal sealed record WorkflowEdgeDto(
	string UpstreamNodeKey,
	string DependentNodeKey,
	string Kind,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowPredicateDto? Predicate);

internal sealed record WorkflowSegmentationDto(int Version, int DurationSeconds, string Digest);

internal sealed record WorkflowDefinitionContentDto(
	string[] RequestedCapabilities,
	string CompletionPolicy,
	WorkflowSegmentationDto SegmentationPolicy,
	WorkflowNodeDto[] Nodes,
	WorkflowEdgeDto[] Edges);

internal sealed record RegistryWorkflowDefinitionDto(
	string WorkflowId,
	string WorkflowVersion,
	string DefinitionDigest,
	WorkflowDefinitionContentDto Definition,
	DateTimeOffset CreatedAt,
	Guid? CreatedBy);

internal sealed record WorkflowListItemDto(
	string WorkflowId,
	string WorkflowVersion,
	string DefinitionDigest,
	DateTimeOffset CreatedAt);

internal sealed record WorkflowListDto(WorkflowListItemDto[] Items);

internal sealed record DefaultWorkflowDto(
	string WorkflowId,
	string WorkflowVersion,
	DateTimeOffset ChangedAt,
	Guid? ChangedBy);
