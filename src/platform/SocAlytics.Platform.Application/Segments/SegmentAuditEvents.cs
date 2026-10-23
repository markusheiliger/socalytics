using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Segments;

public static class SegmentAuditEvents
{
    public const string MatchResourceType = "match";

    public static AuditEvent Purged(Guid matchId, Guid teamId, int purged, int inUse) =>
        new()
        {
            EventType = "segments.purged",
            Action = "purge-segments",
            Outcome = AuditOutcome.Succeeded,
            Resource = new AuditResource(MatchResourceType, matchId.ToString()),
            TeamId = teamId,
            Details = new Dictionary<string, string>
            {
                ["matchId"] = matchId.ToString(),
                ["purgedCount"] = purged.ToString(CultureInfo.InvariantCulture),
                ["inUseCount"] = inUse.ToString(CultureInfo.InvariantCulture),
            },
        };
}
