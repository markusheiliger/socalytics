using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.ViewModels;

public sealed class StatusWindowViewModel : ObservableBase, IDisposable
{
    private readonly IManagerControl _control;
    private readonly Action<Action> _post;

    public StatusWindowViewModel(IManagerControl control, Action<Action>? post = null)
    {
        _control = control;
        _post = post ?? (a => a());
        _control.StatusChanged += OnChanged;
    }

    public string RegistrationState => _control.GetStatus().RegistrationState;

    public string Stamp => _control.GetStatus().StampId ?? "—";

    public string PlatformEndpoint => _control.GetStatus().PlatformEndpoint ?? "—";

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

    public void Dispose() => _control.StatusChanged -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => _post(() =>
    {
        foreach (var name in new[] { nameof(RegistrationState), nameof(Stamp), nameof(PlatformEndpoint), nameof(DeviceFingerprint), nameof(KeyProtection), nameof(Diagnostics) })
        {
            Raise(name);
        }
    });
}
