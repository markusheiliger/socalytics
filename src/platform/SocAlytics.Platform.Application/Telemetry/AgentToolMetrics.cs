using System.Diagnostics.Metrics;

namespace SocAlytics.Platform.Application.Telemetry;

/// <summary>Agent tool instruments. Tags are limited to tool identifier and outcome; ids, arguments, and evidence never become tags.</summary>
public static class AgentToolMetrics
{
    public const string MeterName = "SocAlytics.Platform.AgentTools";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("agent_tool_calls_total");

    public static void CallRecorded(string tool, string outcome) =>
        Calls.Add(1, new KeyValuePair<string, object?>("tool", tool), new KeyValuePair<string, object?>("outcome", outcome));
}
