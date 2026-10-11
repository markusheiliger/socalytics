namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>No Manager-owned work exists in this slice.</summary>
public sealed class NoManagerWork : IManagerWorkTracker
{
    public int ActiveCount => 0;

    public Task WaitForIdleAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAndCleanUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
