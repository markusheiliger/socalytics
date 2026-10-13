using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SocAlytics.Analysts.Manager.Runtime.Simulated;

/// <summary>Simulated runtime with a simulated Analyst; registered only in Development and Test.</summary>
public sealed class SimulatedRuntimeAdapter : IContainerRuntimeAdapter
{
    public const string TestProfile = "simulated";

    private readonly SimulatedAnalystOptions options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, SimulatedAttempt> attempts = new(StringComparer.Ordinal);

    public SimulatedRuntimeAdapter()
        : this(new SimulatedAnalystOptions(), TimeProvider.System)
    {
    }

    public SimulatedRuntimeAdapter(IOptions<SimulatedAnalystOptions> options, TimeProvider time)
        : this(options.Value, time)
    {
    }

    public SimulatedRuntimeAdapter(SimulatedAnalystOptions options, TimeProvider time)
    {
        this.options = options;
        this.time = time;
        Probe = Healthy(options.MaxParallelAttempts);
    }

    public string Profile => TestProfile;

    public RuntimeProbe Probe { get; set; }

    public Exception? Failure { get; set; }

    public IReadOnlyCollection<SimulatedAttempt> Attempts => attempts.Values.ToArray();

    public static ProbeCheck Ok => new(true);

    public static RuntimeProbe Healthy(int maxParallelAttempts = 4) => new("simrt", "2.1.0", "vm", "linux", "6.1.0", "x64",
        Ok, Ok, Ok, Ok, Ok, Ok, Ok, Ok, [new AcceleratorProbe("gpu0", true), new AcceleratorProbe("gpu1", true)],
        maxParallelAttempts);

    public static RuntimeCompatibilityList List(string runtimeRange = ">=2.0.0 <3.0.0") => new(
    [
        new RuntimeCompatibilityEntry(TestProfile, "simrt", runtimeRange, "vm", "linux", "*", "x64",
            ["gpu0", "gpu1"], "*"),
    ]);

    public Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(Probe) : Task.FromException<RuntimeProbe>(Failure);

    public Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<IAttemptExecution>(Failure);
        }

        var behavior = options.Capabilities.TryGetValue(launch.CapabilityId, out var configured)
            ? configured
            : new SimulatedCapabilityOptions();
        var attempt = new SimulatedAttempt(launch, behavior, time);
        attempts[launch.AttemptId] = attempt;
        return Task.FromResult<IAttemptExecution>(attempt);
    }
}

public sealed class SimulatedAttempt : IAttemptExecution
{
    private readonly TaskCompletionSource<AttemptOutcome> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopped = new();

    internal SimulatedAttempt(AttemptLaunch launch, SimulatedCapabilityOptions behavior, TimeProvider time)
    {
        Launch = launch;
        _ = RunAsync(behavior, time);
    }

    public AttemptLaunch Launch { get; }

    public bool Stopped { get; private set; }

    public bool CleanedUp { get; private set; }

    public Task<AttemptOutcome> Outcome => outcome.Task;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        stopped.Cancel();
        return Task.CompletedTask;
    }

    public Task CleanUpAsync(CancellationToken cancellationToken)
    {
        CleanedUp = true;
        return StopAsync(cancellationToken);
    }

    private async Task RunAsync(SimulatedCapabilityOptions behavior, TimeProvider time)
    {
        try
        {
            if (behavior.Outcome == SimulatedOutcome.Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, time, stopped.Token);
            }

            if (behavior.RunDuration > TimeSpan.Zero)
            {
                await Task.Delay(behavior.RunDuration, time, stopped.Token);
            }

            switch (behavior.Outcome)
            {
                case SimulatedOutcome.Succeed:
                    outcome.TrySetResult(AttemptOutcome.Success(
                        behavior.ResultReference ?? $"simulated://results/{Launch.AttemptId}",
                        behavior.ResultChecksum ?? "sha256:" + new string('0', 64)));
                    break;
                case SimulatedOutcome.Fail:
                    outcome.TrySetResult(AttemptOutcome.Failure(behavior.FailureReason));
                    break;
                default:
                    outcome.TrySetException(new InvalidOperationException("The simulated Analyst crashed."));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            outcome.TrySetCanceled(stopped.Token);
        }
    }
}
