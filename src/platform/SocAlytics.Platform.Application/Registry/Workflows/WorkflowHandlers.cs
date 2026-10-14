using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;
using FieldViolation = SocAlytics.Platform.Application.Abstractions.FieldViolation;

namespace SocAlytics.Platform.Application.Registry.Workflows;

public sealed record WorkflowNodeCommand(string? NodeKey, string? ProfileId, int ProfileVersion);

public sealed record WorkflowEdgeCommand(string? UpstreamNodeKey, string? DependentNodeKey, string? Kind, ConditionalPredicate? Predicate);

public sealed record WorkflowSegmentationCommand(int Version, int DurationSeconds, string? Digest);

public sealed record DefineWorkflowCommand(
    string? WorkflowId,
    string? WorkflowVersion,
    IReadOnlyList<string>? RequestedCapabilities,
    string? CompletionPolicy,
    WorkflowSegmentationCommand? SegmentationPolicy,
    IReadOnlyList<WorkflowNodeCommand>? Nodes,
    IReadOnlyList<WorkflowEdgeCommand>? Edges);

public sealed class DefineWorkflowResult
{
    private DefineWorkflowResult()
    {
    }

    public StoredWorkflowDefinition? Definition { get; private init; }

    public OperationFailure? Failure { get; private init; }

    public IReadOnlyList<FieldViolation> Violations { get; private init; } = [];

    public static DefineWorkflowResult Success(StoredWorkflowDefinition definition) => new() { Definition = definition };

    public static DefineWorkflowResult Failed(OperationFailure failure) => new() { Failure = failure };

    public static DefineWorkflowResult Invalid(IReadOnlyList<FieldViolation> violations) => new() { Violations = violations };
}

public sealed class DefineWorkflowHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalystProfileStore profiles,
    ICapabilityDeclarationStore declarations,
    IWorkflowDefinitionStore workflows,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    public const string WorkflowInvalid = "workflow-invalid";
    public const string WorkflowVersionExists = "workflow-version-exists";
    public const string ProfileNotFound = "profile-not-found";

    public async Task<DefineWorkflowResult> HandleAsync(DefineWorkflowCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.WorkflowResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return DefineWorkflowResult.Failed(OperationFailure.Forbidden());
        }

        var shape = CheckShape(command);
        if (shape.Count > 0)
        {
            return DefineWorkflowResult.Failed(OperationFailure.Validation([.. shape]));
        }

        var workflowId = command.WorkflowId!.Trim();
        var workflowVersion = command.WorkflowVersion!.Trim();
        var violations = new List<FieldViolation>();
        var inputs = new List<WorkflowNodeInput>();
        foreach (var node in command.Nodes!)
        {
            var stored = await profiles.FindAsync(node.ProfileId!.Trim(), node.ProfileVersion, cancellationToken);
            var declaration = stored is null ? null : await declarations.FindAsync(stored.Profile.Content.ImageDigest, cancellationToken);
            if (stored is null || declaration is null)
            {
                violations.Add(new FieldViolation($"nodes.{node.NodeKey}", ProfileNotFound));
                continue;
            }

            inputs.Add(new WorkflowNodeInput(node.NodeKey!.Trim(), stored.Profile, declaration.Declaration));
        }

        if (violations.Count > 0)
        {
            return DefineWorkflowResult.Invalid(violations);
        }

        var requested = command.RequestedCapabilities!.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var edges = command.Edges!
            .Select(e => new WorkflowEdgeDefinition(e.UpstreamNodeKey ?? "", e.DependentNodeKey ?? "", e.Kind ?? "", e.Predicate))
            .OrderBy(e => e.UpstreamNodeKey, StringComparer.Ordinal)
            .ThenBy(e => e.DependentNodeKey, StringComparer.Ordinal)
            .ToArray();
        var segmentation = command.SegmentationPolicy!;
        var found = WorkflowValidation.Validate(new WorkflowInput(
            requested,
            command.CompletionPolicy ?? "",
            new SegmentationPolicyInput(segmentation.DurationSeconds, segmentation.Digest),
            inputs,
            edges));
        if (found.Count > 0)
        {
            return DefineWorkflowResult.Invalid(found.Select(v => new FieldViolation("workflow", v.Code)).ToList());
        }

        if (await workflows.FindAsync(workflowId, workflowVersion, cancellationToken) is not null)
        {
            return DefineWorkflowResult.Failed(OperationFailure.Conflict(WorkflowVersionExists));
        }

        var definitionJson = BuildDefinition(requested, command.CompletionPolicy!, segmentation, inputs, edges);
        var digest = ContentDigest.OfCanonical(definitionJson).ToString();
        var definition = new StoredWorkflowDefinition(
            workflowId, workflowVersion, digest, definitionJson, time.GetUtcNow(), context.MemberAccountId);
        if (!await workflows.TryInsertAsync(definition, cancellationToken))
        {
            return DefineWorkflowResult.Failed(OperationFailure.Conflict(WorkflowVersionExists));
        }

        await audit.RecordAsync(RegistryAuditEvents.WorkflowDefined(workflowId, workflowVersion, new Dictionary<string, string>
        {
            ["workflowId"] = workflowId,
            ["workflowVersion"] = workflowVersion,
            ["definitionDigest"] = digest,
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return DefineWorkflowResult.Success(definition);
    }

    private static List<FieldViolation> CheckShape(DefineWorkflowCommand command)
    {
        var violations = new List<FieldViolation>();
        if (string.IsNullOrWhiteSpace(command.WorkflowId))
        {
            violations.Add(new FieldViolation("workflowId", "required"));
        }

        if (string.IsNullOrWhiteSpace(command.WorkflowVersion))
        {
            violations.Add(new FieldViolation("workflowVersion", "required"));
        }

        if (command.RequestedCapabilities is null || command.RequestedCapabilities.Count == 0
            || command.RequestedCapabilities.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add(new FieldViolation("requestedCapabilities", "required"));
        }

        if (command.SegmentationPolicy is null)
        {
            violations.Add(new FieldViolation("segmentationPolicy", "required"));
        }

        if (command.Nodes is null || command.Nodes.Count == 0
            || command.Nodes.Any(n => string.IsNullOrWhiteSpace(n.NodeKey) || string.IsNullOrWhiteSpace(n.ProfileId)))
        {
            violations.Add(new FieldViolation("nodes", "required"));
        }

        if (command.Edges is null)
        {
            violations.Add(new FieldViolation("edges", "required"));
        }

        return violations;
    }

    private static string BuildDefinition(
        string[] requested,
        string completionPolicy,
        WorkflowSegmentationCommand segmentation,
        IReadOnlyList<WorkflowNodeInput> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges)
    {
        Sha256Digest.TryParse(segmentation.Digest, out var segmentationDigest);
        var root = new JsonObject
        {
            ["requestedCapabilities"] = new JsonArray(requested.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["completionPolicy"] = completionPolicy,
            ["segmentationPolicy"] = new JsonObject
            {
                ["version"] = segmentation.Version,
                ["durationSeconds"] = segmentation.DurationSeconds,
                ["digest"] = segmentationDigest!.ToString(),
            },
            ["nodes"] = new JsonArray(nodes.OrderBy(n => n.NodeKey, StringComparer.Ordinal).Select(n => (JsonNode)new JsonObject
            {
                ["nodeKey"] = n.NodeKey,
                ["profileId"] = n.Profile.ProfileId,
                ["profileVersion"] = n.Profile.Version,
                ["profileDigest"] = n.Profile.Digest.ToString(),
            }).ToArray()),
            ["edges"] = new JsonArray(edges.Select(e =>
            {
                var edge = new JsonObject
                {
                    ["upstreamNodeKey"] = e.UpstreamNodeKey,
                    ["dependentNodeKey"] = e.DependentNodeKey,
                    ["kind"] = e.Kind,
                };
                if (e.Predicate is { } p)
                {
                    var predicate = new JsonObject { ["kind"] = p.Kind };
                    if (p.CapabilityId is not null)
                    {
                        predicate["capabilityId"] = p.CapabilityId;
                    }

                    if (p.NodeKey is not null)
                    {
                        predicate["nodeKey"] = p.NodeKey;
                    }

                    edge["predicate"] = predicate;
                }

                return (JsonNode)edge;
            }).ToArray()),
        };
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }
}

public sealed record SetDefaultWorkflowCommand(string? WorkflowId, string? WorkflowVersion, long? ExpectedVersion);

public sealed class SetDefaultWorkflowResult
{
    private SetDefaultWorkflowResult()
    {
    }

    public StoredDefaultWorkflow? Default { get; private init; }

    public OperationFailure? Failure { get; private init; }

    public bool NotResolvable { get; private init; }

    public static SetDefaultWorkflowResult Success(StoredDefaultWorkflow value) => new() { Default = value };

    public static SetDefaultWorkflowResult Failed(OperationFailure failure) => new() { Failure = failure };

    public static SetDefaultWorkflowResult Unresolvable() => new() { NotResolvable = true };
}

public sealed class SetDefaultWorkflowHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalystProfileStore profiles,
    IWorkflowDefinitionStore workflows,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    public const string WorkflowNotResolvable = "workflow-not-resolvable";

    public async Task<SetDefaultWorkflowResult> HandleAsync(SetDefaultWorkflowCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.WorkflowResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return SetDefaultWorkflowResult.Failed(OperationFailure.Forbidden());
        }

        var violations = new List<FieldViolation>();
        if (string.IsNullOrWhiteSpace(command.WorkflowId))
        {
            violations.Add(new FieldViolation("workflowId", "required"));
        }

        if (string.IsNullOrWhiteSpace(command.WorkflowVersion))
        {
            violations.Add(new FieldViolation("workflowVersion", "required"));
        }

        if (violations.Count > 0)
        {
            return SetDefaultWorkflowResult.Failed(OperationFailure.Validation([.. violations]));
        }

        var current = await workflows.GetDefaultAsync(cancellationToken);
        if (current is not null && command.ExpectedVersion is null)
        {
            return SetDefaultWorkflowResult.Failed(OperationFailure.VersionRequired());
        }

        if (current is null ? command.ExpectedVersion is not null : current.Version != command.ExpectedVersion)
        {
            return SetDefaultWorkflowResult.Failed(OperationFailure.VersionMismatch(current?.Version));
        }

        var workflowId = command.WorkflowId!.Trim();
        var workflowVersion = command.WorkflowVersion!.Trim();
        var definition = await workflows.FindAsync(workflowId, workflowVersion, cancellationToken);
        if (definition is null || !await IsResolvableAsync(definition, cancellationToken))
        {
            return SetDefaultWorkflowResult.Unresolvable();
        }

        var changedAt = time.GetUtcNow();
        var write = await workflows.SetDefaultAsync(
            workflowId, workflowVersion, context.MemberAccountId, changedAt, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return SetDefaultWorkflowResult.Failed(write.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(write.Version));
        }

        var details = new Dictionary<string, string>
        {
            ["workflowId"] = workflowId,
            ["workflowVersion"] = workflowVersion,
            ["definitionDigest"] = definition.DefinitionDigest,
        };
        if (current is not null)
        {
            details["previousWorkflowId"] = current.WorkflowId;
            details["previousWorkflowVersion"] = current.WorkflowVersion;
        }

        await audit.RecordAsync(RegistryAuditEvents.DefaultWorkflowChanged(workflowId, workflowVersion, details), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return SetDefaultWorkflowResult.Success(
            new StoredDefaultWorkflow(workflowId, workflowVersion, changedAt, context.MemberAccountId, write.Version ?? 1));
    }

    private async Task<bool> IsResolvableAsync(StoredWorkflowDefinition definition, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(definition.DefinitionJson);
        if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            var digest = node.TryGetProperty("profileDigest", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            var stored = digest is null ? null : await profiles.FindByDigestAsync(digest, cancellationToken);
            if (stored is null || stored.Profile.State != ProfileState.Approved)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ListWorkflowsHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IWorkflowDefinitionStore workflows)
{
    public async Task<OperationResult<IReadOnlyList<StoredWorkflowDefinition>>> HandleAsync(CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.WorkflowResourceType, null), cancellationToken);
        return decision.IsGranted
            ? OperationResult<IReadOnlyList<StoredWorkflowDefinition>>.Success(await workflows.ListAsync(cancellationToken))
            : OperationFailure.Forbidden();
    }
}

public sealed class GetWorkflowHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IWorkflowDefinitionStore workflows)
{
    public async Task<OperationResult<StoredWorkflowDefinition>> HandleAsync(
        string workflowId, string workflowVersion, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource(RegistryAuditEvents.WorkflowResourceType, RegistryAuditEvents.WorkflowResourceId(workflowId, workflowVersion)),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var found = await workflows.FindAsync(workflowId, workflowVersion, cancellationToken);
        return found is null ? OperationFailure.NotFound() : found;
    }
}

public sealed class GetDefaultWorkflowHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IWorkflowDefinitionStore workflows)
{
    public async Task<OperationResult<StoredDefaultWorkflow>> HandleAsync(CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.WorkflowResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var found = await workflows.GetDefaultAsync(cancellationToken);
        return found is null ? OperationFailure.NotFound() : found;
    }
}
