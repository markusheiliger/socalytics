using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.Tests.Support;

public sealed class SimulatedWork : IManagerWorkTracker
{
    private readonly TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ActiveCount { get; private set; }

    public bool Stopped { get; private set; }

    public void Start() => ActiveCount++;

    public void Finish()
    {
        if (ActiveCount > 0 && --ActiveCount == 0)
        {
            idle.TrySetResult();
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken) =>
        ActiveCount == 0 ? Task.CompletedTask : idle.Task.WaitAsync(cancellationToken);

    public Task StopAndCleanUpAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        ActiveCount = 0;
        idle.TrySetResult();
        return Task.CompletedTask;
    }
}
