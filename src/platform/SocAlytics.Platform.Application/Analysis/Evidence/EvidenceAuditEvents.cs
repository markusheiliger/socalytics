using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Analysis.Evidence;

public static class EvidenceAuditEvents
{
    public const string GrantedEventType = "analysis.evidence-media.granted";
    public const string GrantedAction = "grant-evidence-media";
    public const string RunResourceType = "analysis-run";

    // Carries neither links, object keys, nor signature parameters.
    public static AuditEvent Granted(
        Guid runId,
        Guid matchId,
        Guid teamId,
        Guid? acceptedResultId,
        int? ordinal,
        long requestedFromMs,
        long requestedToMs,
        bool shortened,
        IReadOnlyCollection<string> mediaDigests,
        DateTimeOffset latestExpiresAt)
    {
        var details = new Dictionary<string, string>
        {
            ["runId"] = runId.ToString(),
            ["matchId"] = matchId.ToString(),
            ["kind"] = acceptedResultId is null ? "range" : "fact",
            ["fromMs"] = requestedFromMs.ToString(CultureInfo.InvariantCulture),
            ["toMs"] = requestedToMs.ToString(CultureInfo.InvariantCulture),
            ["shortened"] = shortened ? "true" : "false",
            ["grantedPartCount"] = mediaDigests.Count.ToString(CultureInfo.InvariantCulture),
            ["mediaDigests"] = string.Join(',', mediaDigests),
            ["grantExpiresAt"] = latestExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        if (acceptedResultId is { } result)
        {
            details["acceptedResultId"] = result.ToString();
            details["ordinal"] = ordinal!.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new AuditEvent
        {
            EventType = GrantedEventType,
            Action = GrantedAction,
            Outcome = AuditOutcome.Succeeded,
            Resource = new AuditResource(RunResourceType, runId.ToString()),
            TeamId = teamId,
            Details = details,
        };
    }
}
