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

    public ManagerControl(RegistrationCoordinator coordinator, TimeProvider time, IManagerWorkTracker work)
    {
        _work = work;
        _coordinator = coordinator;
        _time = time;
        _started = time.GetUtcNow();
        coordinator.Changed += (_, e) => StatusChanged?.Invoke(this, e);
    }

    public event EventHandler? StatusChanged;

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
            null,
            _coordinator.Diagnostics,
            machine?.Intent.EndUtc,
            machine?.Intent.EndUtc is { } end ? Math.Max(0, (end - _time.GetUtcNow()).TotalSeconds) : null,
            _coordinator.Protection?.ToWireValue(),
            null,
            ControlSurface,
            _coordinator.Fingerprint);
    }

    public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        _coordinator.RegisterAsync(request, cancellationToken);

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
