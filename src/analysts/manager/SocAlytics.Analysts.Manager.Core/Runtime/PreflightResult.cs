namespace SocAlytics.Analysts.Manager.Runtime;

public sealed record PreflightCheck(string Id, bool Passed, string? Detail, string? Remediation);

public sealed record PreflightResult(
    DateTimeOffset RanAt,
    string? Profile,
    string? RuntimeProduct,
    string? RuntimeVersion,
    string? Backend,
    bool Passed,
    IReadOnlyList<PreflightCheck> Checks,
    IReadOnlyList<string> ValidatedCapabilities,
    int MaxParallelAttempts = 0)
{
    /// <summary>The workflow capabilities this Manager offers to the queue; empty unless the preflight passed.</summary>
    public IReadOnlyList<PreflightCapability> Offered { get; init; } = [];

    /// <summary>Workflow capabilities that are not offered, each with its reason.</summary>
    public IReadOnlyList<PreflightCapability> NotOffered { get; init; } = [];

    /// <summary>The last reconciliation of this Manager's leftover runtime resources; null when none ran.</summary>
    public ReconciliationState? Reconciliation { get; init; }
}

/// <summary>What the last reconciliation removed and which leftovers it could not remove (retried by the next run).</summary>
public sealed record ReconciliationState(
    DateTimeOffset RanAt, int RemovedContainers, int RemovedDirectories, IReadOnlyList<string> Leftovers);

/// <summary>A workflow capability in the local status model; <see cref="Reason"/> is set only for capabilities that are not offered.</summary>
public sealed record PreflightCapability(string Id, string Version, string? Reason = null);
