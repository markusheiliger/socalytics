namespace SocAlytics.Analysts.Manager.Runtime.Simulated;

public enum SimulatedOutcome
{
    Succeed,
    Fail,
    Hang,
    Crash,
}

public sealed class SimulatedCapabilityOptions
{
    /// <summary>The capability version the simulated runtime offers to the queue.</summary>
    public string Version { get; set; } = "1.0.0";

    public SimulatedOutcome Outcome { get; set; } = SimulatedOutcome.Succeed;

    public TimeSpan RunDuration { get; set; } = TimeSpan.Zero;

    public string FailureReason { get; set; } = "simulated failure";

    public string? ResultReference { get; set; }

    public string? ResultChecksum { get; set; }
}

/// <summary>Development and Test only; bound from <c>Runtime:Simulated</c>.</summary>
public sealed class SimulatedAnalystOptions
{
    public const string SectionName = "Runtime:Simulated";

    public int MaxParallelAttempts { get; set; } = 4;

    /// <summary>Keyed by capability id; capabilities without an entry succeed.</summary>
    public Dictionary<string, SimulatedCapabilityOptions> Capabilities { get; set; } = new(StringComparer.Ordinal);
}
