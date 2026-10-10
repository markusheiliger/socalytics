using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Hosting;

public sealed class ManagerControl : IManagerControl
{
    private readonly RegistrationCoordinator _coordinator;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;

    private readonly IManagerWorkTracker _work;
    private readonly Runtime.PreflightScheduler _preflight;
    private readonly DrainCoordinator _drain;
    private readonly Autostart.IAutostartRegistrar _autostart;
    private readonly Microsoft.Extensions.Options.IOptions<OperatingOptions> _operating;

    public ManagerControl(RegistrationCoordinator coordinator, TimeProvider time, IManagerWorkTracker work, Runtime.PreflightScheduler preflight,
        DrainCoordinator drain, Microsoft.Extensions.Options.IOptions<OperatingOptions> operating,
        Autostart.IAutostartRegistrar autostart)
    {
        _autostart = autostart;
        _drain = drain;
        _operating = operating;
        _preflight = preflight;
        _work = work;
        _coordinator = coordinator;
        _time = time;
        _started = time.GetUtcNow();
        coordinator.Changed += (_, e) => StatusChanged?.Invoke(this, e);
    }

    public event EventHandler? StatusChanged;

    public event EventHandler? ExitCompleted;

    public string ControlSurface { get; set; } = "none";

    public ManagerStatusSnapshot GetStatus()
    {
        var active = _coordinator.Active;
        var machine = _coordinator.Machine;
        return new ManagerStatusSnapshot(
            _coordinator.State.ToWireValue(),
            _coordinator.Phase?.ToWireValue(),
            active?.ManagerId,
            _coordinator.StampId,
            (active?.PlatformEndpoint ?? _coordinator.PlatformEndpoint)?.GetLeftPart(UriPartial.Authority),
            _coordinator.ClubName,
            _coordinator.Connectivity,
            machine is null ? null : StateWire(machine.State.Kind),
            machine is null ? null : IntentWire(machine.Intent.Kind),
            _coordinator.PendingUnregister,
            machine?.State is { Kind: OperatingStateKind.Draining, Deadline: { } deadline } drain
                ? new DrainStatus(PurposeWire(drain.Purpose), deadline, _work.ActiveCount)
                : null,
            (_time.GetUtcNow() - _started).TotalSeconds,
            machine is null ? null : _preflight.Latest,
            _coordinator.Diagnostics,
            machine?.Intent.EndUtc,
            machine?.Intent.EndUtc is { } end ? Math.Max(0, (end - _time.GetUtcNow()).TotalSeconds) : null,
            _coordinator.Protection?.ToWireValue(),
            ReadAutostart(),
            ControlSurface,
            _coordinator.Fingerprint);
    }

    public async Task<ControlResult> SetAutostartAsync(bool enabled)
    {
        try
        {
            if (enabled)
            {
                await _autostart.EnableAsync(Environment.ProcessPath ?? throw new Autostart.AutostartException("no executable path"));
            }
            else
            {
                await _autostart.DisableAsync();
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return new ControlResult(true);
        }
        catch (Autostart.AutostartException)
        {
            return new ControlResult(false, "autostart-failed");
        }
    }

    private bool? ReadAutostart()
    {
        try
        {
            return _autostart.IsEnabledAsync().GetAwaiter().GetResult();
        }
        catch (Autostart.AutostartException)
        {
            return null;
        }
    }

    public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        _coordinator.RegisterAsync(request, cancellationToken);

    public Task<ControlResult> PauseAsync() => _coordinator.SetPausedAsync(true);

    public Task<ControlResult> PauseAsync(TimeSpan duration) => PauseUntilAsync(_time.GetUtcNow() + duration);

    public Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc)
    {
        var length = endUtc - _time.GetUtcNow();
        if (length < TimeSpan.FromMinutes(1) || length > _operating.Value.MaxTimedPause)
        {
            return Task.FromResult(new ControlResult(false, "invalid-pause-duration"));
        }

        return _coordinator.SetIntentAsync(OperatingIntent.PausedUntil(endUtc));
    }

    public Task<ControlResult> ResumeAsync() => _coordinator.SetPausedAsync(false);

    public Task<ControlResult> ExitAsync() => ExitAsync(DrainPurpose.SafeExit, null);

    public Task<ControlResult> SessionEndingAsync() => ExitAsync(DrainPurpose.SessionEnd, _operating.Value.SessionEndDrainBound);

    private async Task<ControlResult> ExitAsync(DrainPurpose purpose, TimeSpan? bound)
    {
        var machine = _coordinator.Machine;
        if (_coordinator.State != Registration.ManagerRegistrationState.Active || machine is null
            || machine.State.Kind is OperatingStateKind.Draining or OperatingStateKind.Ended || _coordinator.PendingUnregister)
        {
            if (purpose == DrainPurpose.SessionEnd)
            {
                ExitCompleted?.Invoke(this, EventArgs.Empty);
            }

            return new ControlResult(false, "invalid-registration-state");
        }

        var result = await _drain.DrainAsync(purpose, default, bound);
        if (result == DrainResult.Cancelled && purpose == DrainPurpose.SafeExit)
        {
            return new ControlResult(false, "drain-timeout");
        }

        ExitCompleted?.Invoke(this, EventArgs.Empty);
        return new ControlResult(true);
    }

    public Task CancelRegistrationAsync() => _coordinator.CancelAsync();

    public Task<ControlResult> UnregisterAsync() => _coordinator.UnregisterAsync();

    public Task<ControlResult> DiscardLocalRegistrationAsync() => _coordinator.DiscardLocalAsync();

    private static string PurposeWire(DrainPurpose? purpose) => purpose switch
    {
        DrainPurpose.Unregister => "unregister",
        DrainPurpose.SessionEnd => "session-end",
        _ => "safe-exit",
    };

    private static string StateWire(OperatingStateKind kind) => kind switch
    {
        OperatingStateKind.Running => "running",
        OperatingStateKind.Paused => "paused",
        OperatingStateKind.Draining => "draining",
        _ => "runtime-unavailable",
    };

    private static string IntentWire(OperatingIntentKind kind) => kind switch
    {
        OperatingIntentKind.Paused => "paused",
        OperatingIntentKind.PausedUntil => "paused-until",
        _ => "running",
    };
}
