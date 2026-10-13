using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Registry;

public static class RegistryAuditEvents
{
    public const string ResourceType = "analyst-manager-registration";

    public static readonly IReadOnlySet<string> AllowedDetailKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "managerId", "stampId", "keyThumbprint", "fromState", "toState", "windowExpiresAt", "os", "osVersion",
        "architecture", "managerVersion", "claimedKeyProtection", "deviceFingerprint", "pairingSourceAddress",
        "registrationCorrelationId", "fencedAttempts", "revokedCredentials",
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
