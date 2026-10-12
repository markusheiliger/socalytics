using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SocAlytics.Platform.Application.Telemetry;

/// <summary>Workflow instruments. Metric tags are limited to capability, message type, outcome, and failure category.</summary>
public static class AnalysisMetrics
{
    public const string MeterName = "SocAlytics.Platform.Analysis";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LeaseExpiries = Meter.CreateCounter<long>("socalytics.analysis.lease_expiries");
    private static readonly Counter<long> StaleCompletions = Meter.CreateCounter<long>("socalytics.analysis.completions_rejected");
    private static readonly Counter<long> RejectedHeartbeats = Meter.CreateCounter<long>("socalytics.analysis.heartbeats_rejected");
    private static readonly Counter<long> RejectedNotifications = Meter.CreateCounter<long>("socalytics.analysis.notifications_rejected");
    private static readonly Counter<long> BlockedRuns = Meter.CreateCounter<long>("socalytics.analysis.runs_blocked");

    public static void LeaseExpired(string? capability) => LeaseExpiries.Add(1, Tag("capability", capability));

    public static void CompletionRejected(string outcome) => StaleCompletions.Add(1, Tag("outcome", outcome));

    public static void HeartbeatRejected(string outcome) => RejectedHeartbeats.Add(1, Tag("outcome", outcome));

    public static void NotificationRejected(string category) => RejectedNotifications.Add(1, Tag("failure_category", category));

    public static void RunBlocked(string? capability) => BlockedRuns.Add(1, Tag("capability", capability));

    /// <summary>Ids go to traces only, never to metric tags.</summary>
    public static void TagActivity(Guid? runId = null, Guid? logicalJobId = null, Guid? attemptId = null, Guid? managerId = null, string? capability = null)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        Set(activity, "socalytics.run_id", runId);
        Set(activity, "socalytics.logical_job_id", logicalJobId);
        Set(activity, "socalytics.attempt_id", attemptId);
        Set(activity, "socalytics.manager_id", managerId);
        if (capability is not null)
        {
            activity.SetTag("socalytics.capability_id", capability);
        }
    }

    private static void Set(Activity activity, string key, Guid? value)
    {
        if (value is { } v)
        {
            activity.SetTag(key, v.ToString());
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, string? value) => new(key, value ?? "unknown");
}
