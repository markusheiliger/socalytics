using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>Runtime wording shared by the CLI and the status window; local status model only, never part of the platform status report.</summary>
public static class RuntimeStatusText
{
    public static IReadOnlyList<string> Lines(PreflightResult? p)
    {
        if (p is null)
        {
            return ["Runtime: no preflight yet"];
        }

        var lines = new List<string>
        {
            $"Runtime: {p.Profile ?? "none"}, {(p.RuntimeProduct is null ? "version unknown" : $"{p.RuntimeProduct} {p.RuntimeVersion}")}",
            $"Endpoint: {Health(p, "endpoint-reachable", "endpoint-local", "endpoint-authorized")}",
            $"Backend: {p.Backend ?? "unknown"}, {Health(p, "backend-available")}",
        };
        foreach (var c in p.Checks.Where(c => !c.Passed))
        {
            lines.Add($"Failed check {c.Id}: {c.Detail ?? "no details"}{(c.Remediation is null ? "" : $" — {c.Remediation}")}");
        }

        lines.Add(p.Offered.Count == 0
            ? "Offered capabilities: none"
            : $"Offered capabilities: {string.Join(", ", p.Offered.Select(o => $"{o.Id}@{o.Version}"))}");
        lines.AddRange(p.NotOffered.Select(n => $"Not offered: {n.Id}@{n.Version} ({n.Reason ?? "unknown reason"})"));
        if (p.Reconciliation is { } r)
        {
            lines.Add($"Reconciliation: last ran {r.RanAt:u}, removed {r.RemovedContainers} containers and {r.RemovedDirectories} attempt directories");
            lines.AddRange(r.Leftovers.Select(l => $"Could not remove: {l} (retried at the next reconciliation)"));
        }

        if (p.ImageCache is { } cache)
        {
            lines.Add($"Image cache: {cache.Images} images, {FormatBytes(cache.UsedBytes)} of {FormatBytes(cache.BudgetBytes)} budget, last pruned {cache.RanAt:u}, removed {cache.Removed} images");
            lines.AddRange(cache.Leftovers.Select(l => $"Could not remove: {l} (retried at the next image cache pass)"));
        }

        return lines;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MiB"
        : $"{bytes} bytes";

    private static string Health(PreflightResult p, params string[] ids)
    {
        var checks = p.Checks.Where(c => ids.Contains(c.Id)).ToArray();
        return checks.Length == 0 ? "not checked" : checks.All(c => c.Passed) ? "healthy" : "unhealthy";
    }
}

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
