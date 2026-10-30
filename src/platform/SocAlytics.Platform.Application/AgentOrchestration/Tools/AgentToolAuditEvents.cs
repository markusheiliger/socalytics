using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

/// <summary>Agent tool audit events. Details carry identifiers, digests, and reason codes only, never evidence content or links.</summary>
public static class AgentToolAuditEvents
{
    public const string RefusedEvent = "agent.tool.call.refused";
    public const string ScopeLostEvent = "agent.tool.call.scope-lost";
    public const string EvidenceMediaGrantedEvent = "agent.tool.evidence-media.granted";

    public const string CallerNotWorkload = "caller-not-workload";
    public const string AttemptStale = "attempt-stale";
    public const string NotPermitted = "not-permitted";
    public const string CallIdReused = "call-id-reused";
    public const string LimitReached = "limit-reached";

    public const string AttemptResourceType = "agent-attempt";

    public const string ToolDetail = "tool";
    public const string CallIdDetail = "callId";
    public const string InitiatingMemberDetail = "initiatingMemberId";
    public const string MediaDigestsDetail = "mediaDigests";
    public const string GrantExpiresAtDetail = "grantExpiresAt";
    public const string LimitDetail = "limit";

    private const string AttemptIdDetail = AgentOrchestrationAuditEvents.AttemptIdDetail;
    private const string InvocationIdDetail = AgentOrchestrationAuditEvents.InvocationIdDetail;
    private const string ReasonCodeDetail = AgentOrchestrationAuditEvents.ReasonCodeDetail;
    private const string DispositionDetail = AgentOrchestrationAuditEvents.DispositionDetail;

    private static readonly AuditActorOverride WorkloadActor = new(AuditActorKind.AgentWorkload, null);
    private static readonly AuditActorOverride SystemActor = new(AuditActorKind.System, null);

    // Recorded by the authorization result handler: the caller is unknown, so the actor stays the request's own.
    public static AuditEvent CallerNotWorkloadRefused(Guid attemptId, string? toolId) =>
        Refused(attemptId, null, null, toolId, null, CallerNotWorkload, null, null);

    public static AuditEvent AttemptStaleRefused(
        Guid attemptId, Guid invocationId, Guid teamId, string toolId, string callId, string disposition) =>
        Refused(attemptId, invocationId, teamId, toolId, callId, AttemptStale, disposition, WorkloadActor);

    public static AuditEvent NotPermittedRefused(Guid attemptId, Guid invocationId, Guid teamId, string toolId, string callId) =>
        Refused(attemptId, invocationId, teamId, toolId, callId, NotPermitted, null, WorkloadActor);

    public static AuditEvent CallIdReusedRefused(Guid attemptId, Guid invocationId, Guid teamId, string toolId, string callId) =>
        Refused(attemptId, invocationId, teamId, toolId, callId, CallIdReused, null, WorkloadActor);

    // The limit is "calls" or "clip-resolutions".
    public static AuditEvent LimitReachedRefused(
        Guid attemptId, Guid invocationId, Guid teamId, string toolId, string callId, string limit) =>
        Refused(attemptId, invocationId, teamId, toolId, callId, LimitReached, limit, WorkloadActor);

    public static AuditEvent ScopeLost(
        Guid attemptId, Guid invocationId, Guid teamId, Guid initiatingMemberId, string toolId, string callId) =>
        Build(ScopeLostEvent, "fence-attempt", AuditOutcome.Refused, attemptId, teamId, "scope-lost",
            [
                (AttemptIdDetail, attemptId.ToString()),
                (InvocationIdDetail, invocationId.ToString()),
                (ToolDetail, toolId),
                (CallIdDetail, callId),
                (InitiatingMemberDetail, initiatingMemberId.ToString()),
            ],
            SystemActor);

    // Carries media digests and the latest expiry; never the links.
    public static AuditEvent EvidenceMediaGranted(
        Guid attemptId, Guid invocationId, Guid teamId, Guid initiatingMemberId, string toolId, string callId,
        IReadOnlyCollection<string> mediaDigests, DateTimeOffset latestExpiresAt) =>
        Build(EvidenceMediaGrantedEvent, "grant-evidence-media", AuditOutcome.Succeeded, attemptId, teamId, null,
            [
                (AttemptIdDetail, attemptId.ToString()),
                (InvocationIdDetail, invocationId.ToString()),
                (ToolDetail, toolId),
                (CallIdDetail, callId),
                (InitiatingMemberDetail, initiatingMemberId.ToString()),
                (MediaDigestsDetail, string.Join(',', mediaDigests)),
                (GrantExpiresAtDetail, latestExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ],
            WorkloadActor);

    private static AuditEvent Refused(
        Guid attemptId, Guid? invocationId, Guid? teamId, string? toolId, string? callId, string reason, string? extra,
        AuditActorOverride? actor)
    {
        var details = new List<(string Key, string Value)> { (AttemptIdDetail, attemptId.ToString()), (ReasonCodeDetail, reason) };
        Add(details, InvocationIdDetail, invocationId?.ToString());
        Add(details, ToolDetail, toolId);
        Add(details, CallIdDetail, callId);
        Add(details, reason == LimitReached ? LimitDetail : DispositionDetail, extra);
        return Build(RefusedEvent, "call-tool", AuditOutcome.Refused, attemptId, teamId, reason, details, actor);
    }

    private static void Add(List<(string Key, string Value)> details, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            details.Add((key, value));
        }
    }

    private static AuditEvent Build(
        string eventType, string action, AuditOutcome outcome, Guid attemptId, Guid? teamId, string? reason,
        IEnumerable<(string Key, string Value)> details, AuditActorOverride? actor) =>
        new()
        {
            EventType = eventType,
            Action = action,
            Outcome = outcome,
            Resource = new AuditResource(AttemptResourceType, attemptId.ToString()),
            TeamId = teamId,
            ReasonCode = reason,
            Details = details.ToDictionary(d => d.Key, d => d.Value),
            ActorOverride = actor,
        };
}
