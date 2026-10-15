using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed class FakeManagerControl : IManagerControl
{
    public string State { get; set; } = "unregistered";

    public string? Fingerprint { get; set; }

    public event EventHandler? StatusChanged;

    public event EventHandler? ExitCompleted;

    public event EventHandler? ShowStatusRequested;

    public void RequestShowStatus() => ShowStatusRequested?.Invoke(this, EventArgs.Empty);

    public List<string> Calls { get; } = [];

    public string? OperatingState { get; set; }

    public string? Intent { get; set; }

    public object? PreflightValue { get; set; }

    public bool? Autostart { get; set; }

    public double? PauseRemaining { get; set; }

    private Task<ControlResult> Record(string call)
    {
        Calls.Add(call);
        return Task.FromResult(new ControlResult(true));
    }

    public Task<ControlResult> PauseAsync() => Record("pause");

    public Task<ControlResult> PauseAsync(TimeSpan duration) => Record($"pause:{duration.TotalMinutes}");

    public Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc) => Task.FromResult(new ControlResult(true));

    public bool RefuseResume { get; set; }

    public Task<ControlResult> ResumeAsync()
    {
        Calls.Add("resume");
        return Task.FromResult(RefuseResume ? new ControlResult(false, "invalid-registration-state") : new ControlResult(true));
    }

    public Task<ControlResult> ExitAsync() => Record("exit");

    public Task<ControlResult> SessionEndingAsync() => Task.FromResult(new ControlResult(true));

    public SocAlytics.Analysts.Manager.Work.ConcurrencySnapshot Concurrency { get; set; } = new(1, null, null, 0);

    public SocAlytics.Analysts.Manager.Work.ConcurrencySnapshot GetConcurrency() => Concurrency;

    public Task<ControlResult> SetHostLocalConcurrencyAsync(int limit)
    {
        Calls.Add($"concurrency:{limit}");
        Concurrency = Concurrency with { HostLocal = limit };
        return Task.FromResult(new ControlResult(true));
    }

    public Task<ControlResult> SetAutostartAsync(bool enabled) => Record($"autostart:{enabled}");

    public void Raise() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseExit() => ExitCompleted?.Invoke(this, EventArgs.Empty);

    public ManagerStatusSnapshot GetStatus() => new(
        State, null, null, "stamp-1", "https://platform.example", null, new ConnectivityStatus("unknown"), OperatingState, Intent,
        false, null, 0, PreflightValue, [], null, PauseRemaining, "tpm", Autostart, "none", Fingerprint,
        Concurrency, ActiveAttempts, QueueDepth, HostUtilization);

    public HostUtilizationSample? HostUtilization { get; set; }

    public QueueDepthView QueueDepth { get; set; } = new(
        new SocAlytics.Analysts.Manager.Queue.QueueDepthSnapshot(SocAlytics.Analysts.Manager.Queue.QueueDepthStatus.NotYetAvailable, null, null, [], 0, null), null);

    public List<ActiveAttemptStatus> ActiveAttempts { get; } = [];

    public string RegisterFingerprint { get; set; } = "fp-xyz";

    public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RegisterResult(true, null, "ABCD-1234", RegisterFingerprint, "https://platform.example/pair", DateTimeOffset.UtcNow.AddMinutes(10)));

    public Task CancelRegistrationAsync() => Task.CompletedTask;

    public Task<ControlResult> UnregisterAsync() => Record("unregister");

    public Task<ControlResult> DiscardLocalRegistrationAsync() => Record("discard");
}
