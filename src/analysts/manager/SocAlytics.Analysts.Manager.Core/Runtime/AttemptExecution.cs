namespace SocAlytics.Analysts.Manager.Runtime;

public sealed record AttemptResources(double Cpus, long MemoryBytes, IReadOnlyList<string> Accelerators);

/// <summary>Everything the Platform pinned for one attempt; the Manager never chooses images, models, or preprocessing.</summary>
/// <remarks><see cref="InputsDirectory"/> is the staged upstream data the runtime mounts read-only; null for jobs without upstream dependencies.</remarks>
public sealed record AttemptLaunch(
    string AttemptId,
    string LogicalJobId,
    string CapabilityId,
    string CapabilityDigest,
    string Scope,
    string AnalystProfileDigest,
    string ImageDigest,
    string ModelDigest,
    AttemptResources Resources,
    Work.AnalystJobMessage? Job = null,
    int AttemptNumber = 0,
    string? ResultDirectory = null,
    string? InputsDirectory = null);

public sealed record AttemptOutcome
{
    private AttemptOutcome()
    {
    }

    public bool Succeeded { get; private init; }

    public string? ResultReference { get; private init; }

    public string? ResultChecksum { get; private init; }

    public string? FailureReason { get; private init; }

    public static AttemptOutcome Success(string resultReference, string resultChecksum) =>
        new() { Succeeded = true, ResultReference = resultReference, ResultChecksum = resultChecksum };

    public static AttemptOutcome Failure(string reason) => new() { FailureReason = reason };
}

public interface IAttemptExecution
{
    /// <summary>Completes with the Analyst result; faults when the runtime lost the Analyst without an outcome.</summary>
    Task<AttemptOutcome> Outcome { get; }

    Task StopAsync(CancellationToken cancellationToken);

    Task CleanUpAsync(CancellationToken cancellationToken);
}
