using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Analysis.Results;

/// <summary>Analysis result audit events. Details carry identities and reason codes only, never URLs, signatures, or headers.</summary>
public static class AnalysisAuditEvents
{
    public const string ResultGrantRefusedEvent = "analysis.result-grant-refused";
    public const string ResultGrantRefusedAction = "refuse-result-grant";
    public const string UpstreamReadRefusedEvent = "analysis.upstream-read-refused";
    public const string UpstreamReadRefusedAction = "refuse-upstream-read";
    public const string SegmentMediaReadRefusedEvent = "analysis.segment-media-read-refused";
    public const string SegmentMediaReadRefusedAction = "refuse-segment-media-read";
    public const string OutOfScopeReason = "out-of-scope";
    public const string ResultRejectedEvent = "analysis.result-rejected";
    public const string ResultRejectedAction = "reject-result";
    public const string RunCancelledEvent = "analysis.run-cancelled";
    public const string RunCancelledAction = "cancel-analysis-run";
    public const string RunResourceType = "analysis-run";
    public const string PreviousStateDetail = "previousState";
    public const string AttemptResourceType = "analysis-attempt";
    public const string ForeignAttemptReason = "foreign-attempt";

    public const string ManagerIdDetail = "managerId";
    public const string LogicalJobIdDetail = "logicalJobId";
    public const string RunIdDetail = "runId";
    public const string MatchIdDetail = "matchId";

    public static AuditEvent ResultRejected(Guid attemptId, Guid logicalJobId, Guid runId, Guid teamId, Guid managerId, string reason) =>
        new()
        {
            EventType = ResultRejectedEvent,
            Action = ResultRejectedAction,
            Outcome = AuditOutcome.Refused,
            Resource = new AuditResource(AttemptResourceType, attemptId.ToString()),
            TeamId = teamId,
            ReasonCode = reason,
            Details = new Dictionary<string, string>
            {
                [ManagerIdDetail] = managerId.ToString(),
                [LogicalJobIdDetail] = logicalJobId.ToString(),
                [RunIdDetail] = runId.ToString(),
            },
            ActorOverride = new AuditActorOverride(AuditActorKind.AnalystManager, null),
        };

    public static AuditEvent ResultGrantRefused(Guid attemptId, Guid logicalJobId, Guid runId, Guid managerId, string reason) =>
        new()
        {
            EventType = ResultGrantRefusedEvent,
            Action = ResultGrantRefusedAction,
            Outcome = AuditOutcome.Refused,
            Resource = new AuditResource(AttemptResourceType, attemptId.ToString()),
            ReasonCode = reason,
            Details = new Dictionary<string, string>
            {
                [ManagerIdDetail] = managerId.ToString(),
                [LogicalJobIdDetail] = logicalJobId.ToString(),
                [RunIdDetail] = runId.ToString(),
            },
            ActorOverride = new AuditActorOverride(AuditActorKind.AnalystManager, null),
        };

    public static AuditEvent UpstreamReadRefused(Guid attemptId, Guid logicalJobId, Guid runId, Guid managerId, string reason) =>
        new()
        {
            EventType = UpstreamReadRefusedEvent,
            Action = UpstreamReadRefusedAction,
            Outcome = AuditOutcome.Refused,
            Resource = new AuditResource(AttemptResourceType, attemptId.ToString()),
            ReasonCode = reason,
            Details = new Dictionary<string, string>
            {
                [ManagerIdDetail] = managerId.ToString(),
                [LogicalJobIdDetail] = logicalJobId.ToString(),
                [RunIdDetail] = runId.ToString(),
            },
            ActorOverride = new AuditActorOverride(AuditActorKind.AnalystManager, null),
        };

    public static AuditEvent SegmentMediaReadRefused(Guid attemptId, Guid logicalJobId, Guid runId, Guid managerId, string reason) =>
        new()
        {
            EventType = SegmentMediaReadRefusedEvent,
            Action = SegmentMediaReadRefusedAction,
            Outcome = AuditOutcome.Refused,
            Resource = new AuditResource(AttemptResourceType, attemptId.ToString()),
            ReasonCode = reason,
            Details = new Dictionary<string, string>
            {
                [ManagerIdDetail] = managerId.ToString(),
                [LogicalJobIdDetail] = logicalJobId.ToString(),
                [RunIdDetail] = runId.ToString(),
            },
            ActorOverride = new AuditActorOverride(AuditActorKind.AnalystManager, null),
        };

    // The cancellation reason is free text and is deliberately not part of the event.
    public static AuditEvent RunCancelled(Guid runId, Guid matchId, Guid teamId, string previousState) =>
        new()
        {
            EventType = RunCancelledEvent,
            Action = RunCancelledAction,
            Outcome = AuditOutcome.Succeeded,
            Resource = new AuditResource(RunResourceType, runId.ToString()),
            TeamId = teamId,
            Details = new Dictionary<string, string>
            {
                [RunIdDetail] = runId.ToString(),
                [MatchIdDetail] = matchId.ToString(),
                [PreviousStateDetail] = previousState,
            },
        };
}
