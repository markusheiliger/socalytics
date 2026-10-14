using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Registry;

public static class RegistryAuditEvents
{
    public const string ResourceType = "analyst-manager-registration";
    public const string ImageResourceType = "analyst-image";
    public const string ProfileResourceType = "analyst-profile";
    public const string WorkflowResourceType = "analyst-workflow";
    public const string RegistryResourceType = "analyst-registry";

    public static readonly IReadOnlySet<string> AllowedDetailKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "managerId", "stampId", "keyThumbprint", "fromState", "toState", "windowExpiresAt", "os", "osVersion",
        "architecture", "managerVersion", "claimedKeyProtection", "deviceFingerprint", "pairingSourceAddress",
        "registrationCorrelationId", "fencedAttempts", "revokedCredentials",
        "reference", "imageDigest", "capabilityId", "capabilityVersion", "modelDigest",
        "profileId", "profileVersion", "profileDigest", "selfApproval", "workflowId", "workflowVersion", "definitionDigest",
        "previousWorkflowId", "previousWorkflowVersion",
    };

    public static AuditEvent PairingIssued(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.pairing-issued", "pair", AuditOutcome.Succeeded, AuditActorKind.Anonymous, registrationId, null, details);

    public static AuditEvent PairingRefused(Guid? registrationId, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.pairing-refused", "pair", AuditOutcome.Refused, AuditActorKind.Anonymous, registrationId, reasonCode, details);

    public static AuditEvent PendingRequestCreated(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.pending-request-created", "submit", AuditOutcome.Succeeded, AuditActorKind.Member, registrationId, null, details);

    public static AuditEvent SubmissionRefused(Guid registrationId, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.submission-refused", "submit", AuditOutcome.Refused, AuditActorKind.Member, registrationId, reasonCode, details);

    public static AuditEvent Approved(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.approved", "approve", AuditOutcome.Succeeded, AuditActorKind.Member, registrationId, null, details);

    public static AuditEvent Rejected(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.rejected", "reject", AuditOutcome.Succeeded, AuditActorKind.Member, registrationId, "club-admin-rejection", details);

    public static AuditEvent Expired(Guid registrationId, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.expired", "expire", AuditOutcome.Succeeded, AuditActorKind.System, registrationId, reasonCode, details);

    public static AuditEvent ActivationRefused(Guid registrationId, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.activation-refused", "activate", AuditOutcome.Refused, AuditActorKind.Anonymous, registrationId, reasonCode, details);

    public static AuditEvent Activated(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.activated", "activate", AuditOutcome.Succeeded, AuditActorKind.Anonymous, registrationId, null, details);

    public static AuditEvent CredentialIssued(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.credential-issued", "issue-credential", AuditOutcome.Succeeded, AuditActorKind.AnalystManager, registrationId, null, details);

    public static AuditEvent CredentialRefused(Guid registrationId, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.credential-refused", "issue-credential", AuditOutcome.Refused, AuditActorKind.AnalystManager, registrationId, reasonCode, details);

    public static AuditEvent UnregistrationStarted(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.unregistration-started", "begin-unregister", AuditOutcome.Succeeded, AuditActorKind.AnalystManager, registrationId, null, details);

    public static AuditEvent Unregistered(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.unregistered", "unregister", AuditOutcome.Succeeded, AuditActorKind.AnalystManager, registrationId, null, details);

    public static AuditEvent Revoked(Guid registrationId, IReadOnlyDictionary<string, string>? details = null) =>
        Create("analyst-manager.revoked", "revoke", AuditOutcome.Succeeded, AuditActorKind.Member, registrationId, "club-admin-revocation", details);

    public static AuditEvent AnalystImageRegistered(string imageDigest, IReadOnlyDictionary<string, string>? details = null) =>
        CreateImageEvent("registry.analyst-image.registered", AuditOutcome.Succeeded, imageDigest, null, details);

    public static AuditEvent AnalystImageRefused(string? imageDigest, string reasonCode, IReadOnlyDictionary<string, string>? details = null) =>
        CreateImageEvent("registry.analyst-image.refused", AuditOutcome.Refused, imageDigest, reasonCode, details);

    public static AuditEvent AnalystProfileCreated(string profileId, int version, IReadOnlyDictionary<string, string>? details = null) =>
        CreateProfileEvent("registry.analyst-profile.created", "create-profile", profileId, version, details);

    public static AuditEvent AnalystProfileApproved(string profileId, int version, IReadOnlyDictionary<string, string>? details = null) =>
        CreateProfileEvent("registry.analyst-profile.approved", "approve-profile", profileId, version, details);

    public static AuditEvent AnalystProfileRetired(string profileId, int version, IReadOnlyDictionary<string, string>? details = null) =>
        CreateProfileEvent("registry.analyst-profile.retired", "retire-profile", profileId, version, details);

    public static AuditEvent WorkflowDefined(string workflowId, string workflowVersion, IReadOnlyDictionary<string, string>? details = null) =>
        CreateEvent("registry.workflow.defined", "define-workflow", AuditOutcome.Succeeded, WorkflowResourceType, WorkflowResourceId(workflowId, workflowVersion), null, details);

    public static AuditEvent DefaultWorkflowChanged(string workflowId, string workflowVersion, IReadOnlyDictionary<string, string>? details = null) =>
        CreateEvent("registry.default-workflow.changed", "set-default-workflow", AuditOutcome.Succeeded, WorkflowResourceType, WorkflowResourceId(workflowId, workflowVersion), null, details);

    public static AuditEvent RegistrySeeded(IReadOnlyDictionary<string, string>? details = null)
    {
        var auditEvent = CreateEvent("registry.seeded", "seed-registry", AuditOutcome.Succeeded, RegistryResourceType, null, null, details);
        return auditEvent with { ActorOverride = new AuditActorOverride(AuditActorKind.System, null) };
    }

    public static string WorkflowResourceId(string workflowId, string workflowVersion) => $"{workflowId}:{workflowVersion}";

    public static string ProfileResourceId(string profileId, int version) => $"{profileId}:{version}";

    private static AuditEvent CreateProfileEvent(
        string eventType, string action, string profileId, int version, IReadOnlyDictionary<string, string>? details) =>
        CreateEvent(eventType, action, AuditOutcome.Succeeded, ProfileResourceType, ProfileResourceId(profileId, version), null, details);

    private static AuditEvent CreateImageEvent(
        string eventType, AuditOutcome outcome, string? imageDigest, string? reasonCode, IReadOnlyDictionary<string, string>? details) =>
        CreateEvent(eventType, "register-image", outcome, ImageResourceType, imageDigest, reasonCode, details);

    private static AuditEvent CreateEvent(
        string eventType, string action, AuditOutcome outcome, string resourceType, string? resourceId, string? reasonCode,
        IReadOnlyDictionary<string, string>? details)
    {
        if (details is not null)
        {
            foreach (var key in details.Keys)
            {
                if (!AllowedDetailKeys.Contains(key))
                {
                    throw new ArgumentException($"The audit detail key '{key}' is not allowed.", nameof(details));
                }
            }
        }

        return new AuditEvent
        {
            EventType = eventType,
            Action = action,
            Outcome = outcome,
            Resource = new AuditResource(resourceType, resourceId),
            ReasonCode = reasonCode,
            Details = details,
        };
    }

    private static AuditEvent Create(
        string eventType,
        string action,
        AuditOutcome outcome,
        AuditActorKind actor,
        Guid? registrationId,
        string? reasonCode,
        IReadOnlyDictionary<string, string>? details)
    {
        if (details is not null)
        {
            foreach (var key in details.Keys)
            {
                if (!AllowedDetailKeys.Contains(key))
                {
                    throw new ArgumentException($"The audit detail key '{key}' is not allowed.", nameof(details));
                }
            }
        }

        return new AuditEvent
        {
            EventType = eventType,
            Action = action,
            Outcome = outcome,
            Resource = new AuditResource(ResourceType, registrationId?.ToString()),
            ReasonCode = reasonCode,
            Details = details,
            ActorOverride = actor == AuditActorKind.Member ? null : new AuditActorOverride(actor, null),
        };
    }
}
