namespace SocAlytics.Analysts.Manager.Runtime.Simulated;

public enum SimulatedOutcome
{
    Succeed,
    Fail,
    Hang,
    Crash,
}

/// <summary>Result modes of the simulated Analyst; Development and Test only. Every mode but <see cref="Valid"/> deliberately violates one rule.</summary>
public static class SimulatedResultModes
{
    public const string Valid = "valid";
    public const string ScopeMismatch = "scope-mismatch";
    public const string ProvenanceMismatch = "provenance-mismatch";
    public const string LineageMismatch = "lineage-mismatch";
    public const string ArtifactMissing = "artifact-missing";
    public const string ValidityInvalid = "validity-invalid";
    public const string ContractInvalid = "contract-invalid";

    /// <summary>Reports an empty lineage even when upstream results were staged.</summary>
    public const string IgnoreInputs = "ignore-inputs";

    public static readonly IReadOnlyList<string> All =
        [Valid, ScopeMismatch, ProvenanceMismatch, LineageMismatch, ArtifactMissing, ValidityInvalid, ContractInvalid, IgnoreInputs];
}

public sealed class SimulatedCapabilityOptions
{
    /// <summary>The capability version the simulated runtime offers to the queue.</summary>
    public string Version { get; set; } = "1.0.0";

    public SimulatedOutcome Outcome { get; set; } = SimulatedOutcome.Succeed;

    public TimeSpan RunDuration { get; set; } = TimeSpan.Zero;

    public string FailureReason { get; set; } = "simulated failure";

    /// <summary>One of <see cref="SimulatedResultModes"/>; applies to successful attempts.</summary>
    public string ResultMode { get; set; } = SimulatedResultModes.Valid;

    public string? ResultReference { get; set; }

    public string? ResultChecksum { get; set; }
}

/// <summary>Development and Test only; bound from <c>Runtime:Simulated</c>.</summary>
public sealed class SimulatedAnalystOptions
{
    public const string SectionName = "Runtime:Simulated";

    public int MaxParallelAttempts { get; set; } = 4;

    /// <summary>The daemon access the simulated runtime reports: <c>rootless</c>, <c>rootful-group</c>, or unset (unknown).</summary>
    public string? DaemonAccess { get; set; }

    /// <summary>Workflow identity the simulated Analyst records in its manifests; matches the development default workflow.</summary>
    public string WorkflowId { get; set; } = "development-default";

    public string WorkflowVersion { get; set; } = "1";

    /// <summary>Keyed by capability id; capabilities without an entry succeed.</summary>
    public Dictionary<string, SimulatedCapabilityOptions> Capabilities { get; set; } = new(StringComparer.Ordinal);
}
