using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>The bounds of the concurrency limit; unknown bounds are null.</summary>
public sealed record ConcurrencySnapshot(int HostLocal, int? StampMaximum, int? RuntimeMaximum, int Effective);

/// <summary>Computes <c>min(hostLocal, maxConcurrentAnalysts, MaxParallelAttempts)</c> from the live local state, credentials, and preflight.</summary>
public sealed class ConcurrencyLimit(RegistrationCoordinator coordinator, QueueCredentialProvider credentials, PreflightScheduler preflight)
{
    public ConcurrencySnapshot Snapshot()
    {
        var hostLocal = coordinator.HostLocalConcurrency;
        int? stamp = credentials.Current?.MaxConcurrentAnalysts;
        int? runtime = preflight.Latest is { Passed: true } latest ? latest.MaxParallelAttempts : null;
        return new ConcurrencySnapshot(hostLocal, stamp, runtime, Math.Max(0, Math.Min(hostLocal, Math.Min(stamp ?? int.MaxValue, runtime ?? int.MaxValue))));
    }

    /// <summary>The number of attempts that may run at once; zero until the stamp and runtime maximums are both known.</summary>
    public int Effective()
    {
        var snapshot = Snapshot();
        return snapshot.StampMaximum is null || snapshot.RuntimeMaximum is null ? 0 : snapshot.Effective;
    }
}
