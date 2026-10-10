using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.Operating;

public enum DrainResult
{
    Completed,
    Cancelled,
    Revoked,
}

/// <summary>Drains Manager-owned work; never calls the platform and never outlasts timeout plus cleanup bound.</summary>
public sealed class DrainCoordinator(
    AdmissionGate admission, IManagerWorkTracker work, IOptions<OperatingOptions> options, TimeProvider time)
{
    public async Task<DrainResult> DrainAsync(DrainPurpose purpose, CancellationToken ct = default)
    {
        var machine = admission.Machine ?? throw new InvalidOperationException("No confirmed registration.");
        var settings = options.Value;
        var bound = settings.DrainTimeout;

        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, EventArgs e)
        {
            if (machine.IsRevoked)
            {
                revoked.TrySetResult();
            }
        }

        machine.Changed += OnChanged;
        try
        {
            if (machine.IsRevoked)
            {
                return DrainResult.Revoked;
            }

            if (!machine.BeginDrain(purpose, time.GetUtcNow() + bound))
            {
                throw new InvalidOperationException("A drain cannot start in the current operating state.");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idle = work.WaitForIdleAsync(cts.Token);
            var deadline = Task.Delay(bound, time, cts.Token);
            var first = await Task.WhenAny(idle, deadline, revoked.Task);
            await cts.CancelAsync();

            if (first == revoked.Task || machine.IsRevoked)
            {
                return DrainResult.Revoked;
            }

            if (first == idle)
            {
                await idle;
                machine.DrainCompleted();
                return DrainResult.Completed;
            }

            if (settings.DrainTimeoutPolicy == DrainTimeoutPolicy.CancelRequest)
            {
                machine.DrainCancelled();
                return DrainResult.Cancelled;
            }

            using var cleanup = new CancellationTokenSource(settings.CleanupBound, time);
            try
            {
                await work.StopAndCleanUpAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
            }

            if (machine.IsRevoked)
            {
                return DrainResult.Revoked;
            }

            machine.DrainCompleted();
            return DrainResult.Completed;
        }
        finally
        {
            machine.Changed -= OnChanged;
        }
    }
}
