using Microsoft.Extensions.Hosting;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>Ends a timed pause at its local end time; checks once per second.</summary>
public sealed class TimedPauseScheduler(RegistrationCoordinator coordinator, TimeProvider time) : BackgroundService
{
    /// <summary>Ends the pause when its end time passed; returns true when it ended.</summary>
    public async Task<bool> CheckAsync()
    {
        var machine = coordinator.Machine;
        if (machine is null || machine.Intent is not { Kind: OperatingIntentKind.PausedUntil, EndUtc: { } end }
            || end > time.GetUtcNow())
        {
            return false;
        }

        var result = await coordinator.SetIntentAsync(OperatingIntent.Running);
        return result.Succeeded;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
            }
        }
    }
}
