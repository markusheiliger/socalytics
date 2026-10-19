using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.ViewModels;

public sealed class StatusWindowViewModel : ObservableBase, IDisposable
{
    private static readonly string[] Names =
    [
        nameof(RegistrationState), nameof(Stamp), nameof(Club), nameof(PlatformEndpoint), nameof(Connectivity), nameof(OperatingState),
        nameof(Uptime), nameof(Preflight), nameof(Runtime), nameof(PauseRemaining), nameof(DrainDeadline), nameof(DeviceFingerprint),
        nameof(KeyProtection), nameof(Diagnostics), nameof(EffectiveConcurrency), nameof(ActiveAttempts),
        nameof(WaitingWork), nameof(HostUtilization), nameof(UpdateRequired), nameof(UpdateNotice),
    ];

    private readonly IManagerControl _control;
    private readonly Action<Action> _post;

    public StatusWindowViewModel(IManagerControl control, Action<Action>? post = null)
    {
        _control = control;
        _post = post ?? (a => a());
        Commands = new TrayMenuViewModel(control, _post);
        _control.StatusChanged += OnChanged;
    }

    /// <summary>The same commands and enabled rules as the tray menu.</summary>
    public TrayMenuViewModel Commands { get; }

    public bool UpdateRequired => _control.GetStatus().UpdateRequired is { Required: true };

    public string UpdateNotice => _control.GetStatus().UpdateRequired is { Required: true } u ? u.Sentence : "";

    public string RegistrationState => _control.GetStatus().RegistrationState;

    public string Stamp => _control.GetStatus().StampId ?? "—";

    public string Club => _control.GetStatus().ClubName ?? "—";

    public string PlatformEndpoint => _control.GetStatus().PlatformEndpoint ?? "—";

    public string Connectivity => _control.GetStatus().Connectivity.State;

    public string OperatingState
    {
        get
        {
            var s = _control.GetStatus();
            return s.OperatingState is null ? "—" : s.OperatingIntent is null ? s.OperatingState : $"{s.OperatingState} (intent: {s.OperatingIntent})";
        }
    }

    public string Uptime => TimeSpan.FromSeconds(Math.Floor(_control.GetStatus().UptimeSeconds)).ToString();

    public string Preflight
    {
        get
        {
            if (_control.GetStatus().Preflight is not PreflightResult p)
            {
                return "No preflight yet";
            }

            var lines = new List<string> { $"{(p.Passed ? "Passed" : "Failed")} at {p.RanAt:u}" };
            lines.AddRange(p.Checks.Where(c => !c.Passed).Select(c => $"{c.Id}: {c.Detail}{(c.Remediation is null ? "" : $" — {c.Remediation}")}"));
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>The "Runtime" section, with the same wording as the CLI.</summary>
    public string Runtime => string.Join(Environment.NewLine,
        SocAlytics.Analysts.Manager.Diagnostics.RuntimeStatusText.Lines(_control.GetStatus().Preflight as PreflightResult));

    public string PauseRemaining => TrayMenuViewModel.FormatRemaining(_control.GetStatus().PauseRemainingSeconds) is { Length: > 0 } r ? r : "—";

    public string DrainDeadline => _control.GetStatus().Drain is { } d ? $"{d.Purpose}: {d.Deadline:u} ({d.ActiveWork} active)" : "—";

    public string DeviceFingerprint => _control.GetStatus().DeviceFingerprint ?? "—";

    public string KeyProtection => _control.GetStatus().KeyProtection ?? "—";

    public string Diagnostics
    {
        get
        {
            var list = _control.GetStatus().Diagnostics;
            return list.Count == 0 ? "None" : string.Join(Environment.NewLine, list.Select(d => $"{d.Code}: {d.Message}"));
        }
    }

    public string EffectiveConcurrency
    {
        get
        {
            var c = _control.GetStatus().Concurrency;
            return $"{c.Effective} (host-local {c.HostLocal}, stamp maximum {c.StampMaximum?.ToString() ?? "unknown"}, runtime maximum {c.RuntimeMaximum?.ToString() ?? "unknown"})";
        }
    }

    public IReadOnlyList<ActiveAttemptRow> ActiveAttempts => [.. _control.GetStatus().ActiveAttempts.Select(a => new ActiveAttemptRow(
        a.LogicalJobId.ToString(), a.AttemptNumber, a.Capability, a.ExecutionState, a.StartedAt.ToString("u"),
        a.LastConfirmedHeartbeatAt?.ToString("u") ?? "—"))];

    /// <summary>The "Waiting work (approximate)" section, with the same wording as the CLI.</summary>
    public string WaitingWork => string.Join(Environment.NewLine, _control.GetStatus().QueueDepth.Lines());

    /// <summary>The "Host" section, with the same wording as the CLI.</summary>
    public string HostUtilization => string.Join(Environment.NewLine, SocAlytics.Analysts.Manager.Diagnostics.HostUtilizationText.Lines(_control.GetStatus().HostUtilization));

    public void Dispose()
    {
        _control.StatusChanged -= OnChanged;
        Commands.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e) => _post(() =>
    {
        foreach (var name in Names)
        {
            Raise(name);
        }
    });
}

public sealed record ActiveAttemptRow(string LogicalJobId, int AttemptNumber, string Capability, string ExecutionState, string StartedAt, string LastConfirmedHeartbeat)
{
    public string Summary => $"{LogicalJobId} #{AttemptNumber} · {Capability} · {ExecutionState} · started {StartedAt} · heartbeat {LastConfirmedHeartbeat}";
}
