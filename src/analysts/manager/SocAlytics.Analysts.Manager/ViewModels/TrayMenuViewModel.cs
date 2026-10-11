using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.ViewModels;

public sealed class TrayMenuViewModel : ObservableBase, IDisposable
{
    public static readonly IReadOnlyList<(string Label, TimeSpan Duration)> Presets =
    [
        ("30 min", TimeSpan.FromMinutes(30)),
        ("1 h", TimeSpan.FromHours(1)),
        ("2 h", TimeSpan.FromHours(2)),
        ("4 h", TimeSpan.FromHours(4)),
        ("8 h", TimeSpan.FromHours(8)),
    ];

    private static readonly string[] Names =
    [
        nameof(RegisterEnabled), nameof(PauseEnabled), nameof(ResumeEnabled), nameof(UnregisterEnabled),
        nameof(DiscardEnabled), nameof(AutostartChecked), nameof(PauseRemaining), nameof(ResumeText),
    ];

    private readonly IManagerControl _control;
    private readonly Action<Action> _post;

    public TrayMenuViewModel(IManagerControl control, Action<Action>? post = null)
    {
        _control = control;
        _post = post ?? (a => a());
        _control.StatusChanged += OnChanged;
    }

    public TimeSpan MaxTimedPause { get; set; } = TimeSpan.FromDays(7);

    public bool RegisterEnabled => _control.GetStatus().RegistrationState == "unregistered";

    public bool PauseEnabled => _control.GetStatus() is { RegistrationState: "active" } s && s.OperatingState != "draining";

    public bool ResumeEnabled => _control.GetStatus() is { RegistrationState: "active", OperatingIntent: "paused" or "paused-until" };

    public bool UnregisterEnabled => _control.GetStatus().RegistrationState == "active";

    public bool DiscardEnabled => _control.GetStatus().RegistrationState == "restore-failed";

    public bool AutostartChecked => _control.GetStatus().AutostartEnabled == true;

    public string PauseRemaining => FormatRemaining(_control.GetStatus().PauseRemainingSeconds);

    public string ResumeText => PauseRemaining.Length == 0 ? "Resume" : $"Resume ({PauseRemaining} left)";

    public static string FormatRemaining(double? seconds)
    {
        if (seconds is not { } s)
        {
            return "";
        }

        var t = TimeSpan.FromSeconds(Math.Ceiling(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)Math.Ceiling(t.TotalMinutes)} min";
    }

    public bool TryParseCustom(string? minutes, out TimeSpan duration)
    {
        duration = default;
        if (!double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m))
        {
            return false;
        }

        var d = TimeSpan.FromMinutes(m);
        if (double.IsNaN(m) || d < TimeSpan.FromMinutes(1) || d > MaxTimedPause)
        {
            return false;
        }

        duration = d;
        return true;
    }

    public Task<ControlResult> PauseAsync() => _control.PauseAsync();

    public Task<ControlResult> PauseForAsync(TimeSpan duration) => _control.PauseAsync(duration);

    public Task<ControlResult> ResumeAsync() => _control.ResumeAsync();

    public Task<ControlResult> SafeExitAsync() => _control.ExitAsync();

    public Task<ControlResult> UnregisterAsync() => _control.UnregisterAsync();

    public Task<ControlResult> DiscardLocalAsync() => _control.DiscardLocalRegistrationAsync();

    public Task<ControlResult> ToggleAutostartAsync() => _control.SetAutostartAsync(!AutostartChecked);

    public void Dispose() => _control.StatusChanged -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => _post(() =>
    {
        foreach (var name in Names)
        {
            Raise(name);
        }
    });
}
