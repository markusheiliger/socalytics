using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

/// <summary>Agent catalog audit events. Details carry kind, identifier, version, digest, agent identifier, and violation codes only, never prompt, rationale, or provenance text.</summary>
public static class AgentCatalogAuditEvents
{
    public const string RecordRegisteredEvent = "agent.catalog.record.registered";
    public const string VersionApprovedEvent = "agent.catalog.version.approved";
    public const string VersionRetiredEvent = "agent.catalog.version.retired";
    public const string EnablementChangedEvent = "agent.catalog.enablement.changed";
    public const string AccessRefusedEvent = "agent.catalog.access.refused";

    public const string ArtifactResourceType = "agent-catalog-artifact";
    public const string AgentVersionResourceType = "agent-version";
    public const string EnablementResourceType = "agent-enablement";
    public const string CatalogResourceType = "agent-catalog";

    public const string AgentVersionKind = "agent-version";

    public const string KindDetail = "kind";
    public const string ArtifactIdDetail = "artifactId";
    public const string ArtifactVersionDetail = "artifactVersion";
    public const string DigestDetail = "digest";
    public const string AgentIdDetail = "agentId";
    public const string PreviousVersionDetail = "previousVersion";
    public const string NewVersionDetail = "newVersion";
    public const string SelfApprovalDetail = "selfApproval";
    public const string DifferentiationAcknowledgedDetail = "differentiationAcknowledged";
    public const string ViolationCodesDetail = "violationCodes";
    public const string ReasonCodeDetail = "reasonCode";

    public const string Accepted = "accepted";
    public const string Replayed = "replayed";

    private static readonly AuditActorOverride SystemActor = new(AuditActorKind.System, null);

    public static AuditEvent RecordAccepted(string kind, string id, string version, string digest, bool system = false) =>
        Registration(AuditOutcome.Succeeded, kind, id, version, digest, null, null, system);

    public static AuditEvent RecordReplayed(string kind, string id, string version, string digest, bool system = false) =>
        Registration(AuditOutcome.Unchanged, kind, id, version, digest, null, null, system);

    public static AuditEvent RecordRefused(string kind, string? id, string? version, IEnumerable<string> violationCodes, string reason) =>
        Registration(AuditOutcome.Refused, kind, id, version, null, violationCodes, reason, false);

    public static AuditEvent VersionApproved(string agentId, string version, string digest, bool selfApproval, bool differentiationAcknowledged) =>
        Build(VersionApprovedEvent, "approve-agent-version", AuditOutcome.Succeeded, AgentVersionResourceType, agentId, null,
            Pairs((AgentIdDetail, agentId), (ArtifactVersionDetail, version), (DigestDetail, digest),
                (SelfApprovalDetail, Flag(selfApproval)), (DifferentiationAcknowledgedDetail, Flag(differentiationAcknowledged))));

    public static AuditEvent VersionRetired(string agentId, string version, string digest) =>
        Build(VersionRetiredEvent, "retire-agent-version", AuditOutcome.Succeeded, AgentVersionResourceType, agentId, null,
            Pairs((AgentIdDetail, agentId), (ArtifactVersionDetail, version), (DigestDetail, digest)));

    public static AuditEvent EnablementChanged(string agentId, string? previousVersion, string? newVersion, bool system = false) =>
        Build(EnablementChangedEvent, "set-agent-enablement", AuditOutcome.Succeeded, EnablementResourceType, agentId, null,
            Pairs((AgentIdDetail, agentId), (PreviousVersionDetail, previousVersion), (NewVersionDetail, newVersion)), system ? SystemActor : null);

    public static AuditEvent AccessRefused(string action) =>
        Build(AccessRefusedEvent, action, AuditOutcome.Denied, CatalogResourceType, null, "forbidden", []);

    private static AuditEvent Registration(
        AuditOutcome outcome, string kind, string? id, string? version, string? digest, IEnumerable<string>? violationCodes, string? reason, bool system) =>
        Build(RecordRegisteredEvent, "register-agent-catalog-record", outcome, ArtifactResourceType,
            id is null ? null : $"{kind}/{id}" + (version is null ? string.Empty : $"/{version}"), reason,
            Pairs(
                (KindDetail, kind),
                (ArtifactIdDetail, id),
                (ArtifactVersionDetail, version),
                (DigestDetail, digest),
                (ViolationCodesDetail, violationCodes is null ? null : string.Join(',', violationCodes.Distinct(StringComparer.Ordinal))),
                (ReasonCodeDetail, reason)),
            system ? SystemActor : null);

    private static string Flag(bool value) => value ? "true" : "false";

    private static AuditEvent Build(
        string eventType,
        string action,
        AuditOutcome outcome,
        string resourceType,
        string? resourceId,
        string? reason,
        IEnumerable<(string Key, string Value)> details,
        AuditActorOverride? actor = null) =>
        new()
        {
            EventType = eventType,
            Action = action,
            Outcome = outcome,
            Resource = new AuditResource(resourceType, resourceId),
            ReasonCode = reason,
            Details = details.ToDictionary(d => d.Key, d => d.Value),
            ActorOverride = actor,
        };

    // Absent values are left out so details never carry empty entries.
    private static IEnumerable<(string Key, string Value)> Pairs(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => (p.Key, p.Value!));
}
