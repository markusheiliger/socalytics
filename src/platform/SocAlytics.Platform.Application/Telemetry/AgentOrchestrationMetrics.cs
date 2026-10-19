using System.Diagnostics.Metrics;

namespace SocAlytics.Platform.Application.Telemetry;

/// <summary>Agent orchestration instruments. Metric tags are limited to agent identifier and outcome; ids and text never become tags.</summary>
public static class AgentOrchestrationMetrics
{
    public const string MeterName = "SocAlytics.Platform.AgentOrchestration";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Submissions = Meter.CreateCounter<long>("socalytics.agents.submissions");
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("socalytics.agents.claims");
    private static readonly Counter<long> Completions = Meter.CreateCounter<long>("socalytics.agents.completions");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("socalytics.agents.failures");
    private static readonly Counter<long> LeaseExpiries = Meter.CreateCounter<long>("socalytics.agents.lease_expiries");
    private static readonly Counter<long> ScopeLosses = Meter.CreateCounter<long>("socalytics.agents.scope_losses");
    private static long queuedInvocations;

    static AgentOrchestrationMetrics()
    {
        Meter.CreateObservableGauge("socalytics.agents.invocations_queued", () => Volatile.Read(ref queuedInvocations));
    }

    public static void SetQueuedInvocations(long count) => Volatile.Write(ref queuedInvocations, Math.Max(0, count));

    public static void SubmissionRecorded(string outcome) => Submissions.Add(1, Tag("outcome", outcome));

    public static void ClaimRecorded(string outcome) => Claims.Add(1, Tag("outcome", outcome));

    public static void CompletionRecorded(string outcome) => Completions.Add(1, Tag("outcome", outcome));

    public static void FailureRecorded(string reason) => Failures.Add(1, Tag("failure_reason", reason));

    public static void LeaseExpired(string reason) => LeaseExpiries.Add(1, Tag("reason", reason));

    public static void ScopeLost(string outcome) => ScopeLosses.Add(1, Tag("outcome", outcome));

    private static KeyValuePair<string, object?> Tag(string key, string? value) => new(key, value ?? "unknown");
}
