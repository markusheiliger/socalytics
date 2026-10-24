using System.Diagnostics.Metrics;

namespace SocAlytics.Platform.Application.Telemetry;

/// <summary>Agent catalog instruments. Tags are limited to record kind and outcome; record content never appears.</summary>
public static class AgentCatalogMetrics
{
    public const string MeterName = "SocAlytics.Platform.AgentCatalog";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Registrations = Meter.CreateCounter<long>("agent_catalog.registrations");
    private static readonly Counter<long> Approvals = Meter.CreateCounter<long>("agent_catalog.approvals");
    private static readonly Counter<long> Retirements = Meter.CreateCounter<long>("agent_catalog.retirements");
    private static readonly Counter<long> EnablementChanges = Meter.CreateCounter<long>("agent_catalog.enablement_changes");

    public static void RegistrationRecorded(string kind, string outcome) =>
        Registrations.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));

    public static void ApprovalRecorded() => Approvals.Add(1);

    public static void RetirementRecorded() => Retirements.Add(1);

    public static void EnablementChanged() => EnablementChanges.Add(1);
}
