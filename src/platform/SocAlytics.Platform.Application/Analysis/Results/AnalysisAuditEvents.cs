using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Analysis.Results;

/// <summary>Analysis result audit events. Details carry identities and reason codes only, never URLs, signatures, or headers.</summary>
public static class AnalysisAuditEvents
{
    public const string ResultGrantRefusedEvent = "analysis.result-grant-refused";
    public const string ResultGrantRefusedAction = "refuse-result-grant";
    public const string AttemptResourceType = "analysis-attempt";
    public const string ForeignAttemptReason = "foreign-attempt";

    public const string ManagerIdDetail = "managerId";
    public const string LogicalJobIdDetail = "logicalJobId";
    public const string RunIdDetail = "runId";

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
}
