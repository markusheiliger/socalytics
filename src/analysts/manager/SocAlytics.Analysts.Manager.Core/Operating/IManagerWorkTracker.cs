namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>Tracks Manager-owned work so a drain can wait for it or stop it.</summary>
public interface IManagerWorkTracker
{
    int ActiveCount { get; }

    Task WaitForIdleAsync(CancellationToken cancellationToken);

    /// <summary>Stops remaining work and cleans up; the token carries the cleanup bound.</summary>
    Task StopAndCleanUpAsync(CancellationToken cancellationToken);
}
