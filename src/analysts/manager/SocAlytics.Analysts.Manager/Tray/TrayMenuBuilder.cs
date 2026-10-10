using Avalonia.Controls;
using SocAlytics.Analysts.Manager.ViewModels;

namespace SocAlytics.Analysts.Manager.Tray;

public static class TrayMenuBuilder
{
    public static NativeMenu Build(TrayMenuViewModel model, Action showStatus, Action showRegister, Action exit)
    {
        var status = new NativeMenuItem("Status");
        status.Click += (_, _) => showStatus();
        var register = new NativeMenuItem("Register…") { IsEnabled = model.RegisterEnabled };
        register.Click += (_, _) => showRegister();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrayMenuViewModel.RegisterEnabled))
            {
                register.IsEnabled = model.RegisterEnabled;
            }
        };
        var quit = new NativeMenuItem("Safe exit");
        quit.Click += (_, _) => exit();
        return [status, register, quit];
    }
}
