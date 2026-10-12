using System.Diagnostics.Metrics;

namespace SocAlytics.Platform.Application.Telemetry;

public sealed record OutboxBacklog(long Pending, long Failed, double OldestPendingAgeSeconds);

public static class MessagingMetrics
{
    public const string MeterName = "SocAlytics.Platform.Messaging";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("socalytics.messaging.published");
    private static readonly Counter<long> PublishFailed = Meter.CreateCounter<long>("socalytics.messaging.publish_failed");
    private static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("socalytics.messaging.publication_lag", "s");
    private static OutboxBacklog backlog = new(0, 0, 0);

    static MessagingMetrics()
    {
        Meter.CreateObservableGauge("socalytics.messaging.outbox_pending", () => Volatile.Read(ref backlog).Pending);
        Meter.CreateObservableGauge("socalytics.messaging.outbox_failed", () => Volatile.Read(ref backlog).Failed);
        Meter.CreateObservableGauge("socalytics.messaging.outbox_oldest_pending_age", () => Volatile.Read(ref backlog).OldestPendingAgeSeconds, "s");
    }

    public static void SetBacklog(OutboxBacklog value) => Volatile.Write(ref backlog, value);

    public static void RecordPublished(string subject, double lagSeconds)
    {
        var tag = new KeyValuePair<string, object?>("message_type", subject);
        Published.Add(1, tag);
        Lag.Record(Math.Max(0, lagSeconds), tag);
    }

    public static void RecordFailed(string subject, string category) =>
        PublishFailed.Add(1, new KeyValuePair<string, object?>("message_type", subject), new KeyValuePair<string, object?>("failure_category", category));
}
