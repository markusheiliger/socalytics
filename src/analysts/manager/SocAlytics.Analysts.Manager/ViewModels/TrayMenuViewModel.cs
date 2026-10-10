using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.ViewModels;

public sealed class TrayMenuViewModel : ObservableBase, IDisposable
{
    private readonly IManagerControl _control;
    private readonly Action<Action> _post;

    public TrayMenuViewModel(IManagerControl control, Action<Action>? post = null)
    {
        _control = control;
        _post = post ?? (a => a());
        _control.StatusChanged += OnChanged;
    }

    public bool RegisterEnabled => _control.GetStatus().RegistrationState == "unregistered";

    public void Dispose() => _control.StatusChanged -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => _post(() => Raise(nameof(RegisterEnabled)));
}
