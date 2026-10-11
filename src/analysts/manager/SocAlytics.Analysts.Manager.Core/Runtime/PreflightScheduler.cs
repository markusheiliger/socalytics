using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Runtime;

/// <summary>
/// Runs the preflight whenever a new operating-state machine appears (activation or confirmed restore) and
/// then every <see cref="RuntimeOptions.PreflightInterval"/> while that machine exists.
/// </summary>
public sealed class PreflightScheduler : BackgroundService
{
    private readonly RegistrationCoordinator coordinator;
    private readonly PreflightRunner runner;
    private readonly IOptions<RuntimeOptions> options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim signal = new(0);
    private volatile PreflightResult? latest;

    public PreflightScheduler(
        RegistrationCoordinator coordinator, PreflightRunner runner, IOptions<RuntimeOptions> options, TimeProvider time)
    {
        this.coordinator = coordinator;
        this.runner = runner;
        this.options = options;
        this.time = time;
        coordinator.Changed += (_, _) =>
        {
            try { signal.Release(); } catch (SemaphoreFullException) { }
        };
    }

    public PreflightResult? Latest => latest;

    /// <summary>Runs one preflight against the current state machine; does nothing without one.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var machine = coordinator.Machine;
        if (machine is null || machine.IsRevoked)
        {
            latest = null;
            return;
        }

        var result = await runner.RunAsync(cancellationToken);
        if (!ReferenceEquals(machine, coordinator.Machine) || machine.IsRevoked)
        {
            return;
        }

        latest = result;
        _ = result.Passed ? machine.PreflightPassed() : machine.PreflightFailed();
        coordinator.NotifyChanged();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OperatingStateMachine? last = null;
        var next = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var machine = coordinator.Machine;
            if (machine is null || machine.IsRevoked)
            {
                last = null;
                latest = null;
                await signal.WaitAsync(stoppingToken);
                continue;
            }

            var now = time.GetUtcNow();
            if (!ReferenceEquals(machine, last) || now >= next)
            {
                last = machine;
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    machine.PreflightFailed();
                }

                next = time.GetUtcNow() + options.Value.PreflightInterval;
                continue;
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delay = Task.Delay(next - now, time, wake.Token);
            var changed = signal.WaitAsync(wake.Token);
            await Task.WhenAny(delay, changed);
            wake.Cancel();
            await Task.WhenAll(delay, changed).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
