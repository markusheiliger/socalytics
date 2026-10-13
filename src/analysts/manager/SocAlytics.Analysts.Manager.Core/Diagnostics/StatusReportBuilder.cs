using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>Builds the status report sent with every registration check while the registration is Active.</summary>
public sealed class StatusReportBuilder(
    RegistrationCoordinator coordinator, PreflightScheduler preflight, IManagerWorkTracker work)
{
    /// <summary>Concurrency limit until Analyst Manager Work Execution provides a configurable one.</summary>
    private const int DefaultConcurrencyLimit = 1;

    private const int MaxAnalystsBound = 1024;

    private static readonly string Version =
        typeof(StatusReportBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Returns null while no reportable operating state exists (no machine, or the Manager has ended).</summary>
    public StatusReport? Build()
    {
        if (coordinator.Machine is not { } machine)
        {
            return null;
        }

        var state = machine.State;
        var (wire, pausedUntil, reason) = state.Kind switch
        {
            OperatingStateKind.Running => ("running", (DateTimeOffset?)null, (string?)null),
            OperatingStateKind.Paused => ("paused", PauseEnd(machine.Intent), null),
            OperatingStateKind.RuntimeUnavailable => ("runtime-unavailable", null, FirstFailedCheck()),
            OperatingStateKind.Draining => ("draining", null, null),
            _ => (null, null, null),
        };

        if (wire is null)
        {
            return null;
        }

        var active = Math.Clamp(work.ActiveCount, 0, MaxAnalystsBound);
        var max = Math.Clamp(Math.Max(DefaultConcurrencyLimit, active), 0, MaxAnalystsBound);
        return new StatusReport(wire, pausedUntil, reason, active, max, Truncate(Version, 64));
    }

    private static DateTimeOffset? PauseEnd(OperatingIntent intent) =>
        intent.Kind == OperatingIntentKind.PausedUntil ? intent.EndUtc : null;

    private string? FirstFailedCheck()
    {
        var id = preflight.Latest?.Checks.FirstOrDefault(c => !c.Passed)?.Id;
        return id is null ? null : Truncate(id, 128);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
