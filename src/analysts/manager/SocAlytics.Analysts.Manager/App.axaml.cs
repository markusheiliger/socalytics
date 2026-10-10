using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Tray;
using SocAlytics.Analysts.Manager.ViewModels;
using SocAlytics.Analysts.Manager.Views;

namespace SocAlytics.Analysts.Manager;

public partial class App : Application
{
    private StatusWindow? _status;
    private RegisterWindow? _register;
    private TrayIcon? _tray;
    private bool _sessionEnding;

    public IManagerControl? Control { get; set; }

    public bool Autostarted { get; set; }

    public TrayHostDetector Detector { get; set; } = new();

    public bool StatusWindowOpen => _status is not null;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Control is not null)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Control.ExitCompleted += (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown());
            Control.ShowStatusRequested += (_, _) => Dispatcher.UIThread.Post(ShowStatus);
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_sessionEnding)
                {
                    return;
                }

                e.Cancel = true;
                _sessionEnding = true;
                _ = Control.SessionEndingAsync();
            };
            var trayCreated = Detector.IsTrayAvailable() && TryCreateTray(desktop);
            if (!trayCreated && !(Autostarted && Detector.IsTrayAvailable()))
            {
                ShowStatus();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ShowStatusIfNoTray()
    {
        if (!Detector.IsTrayAvailable())
        {
            ShowStatus();
        }
    }

    public void ShowStatus()
    {
        if (_status is null)
        {
            _status = new StatusWindow
            {
                DataContext = new StatusWindowViewModel(Control!, a => Dispatcher.UIThread.Post(a)),
                RegisterRequested = ShowRegister,
            };
            _status.Closed += (_, _) => _status = null;
        }

        _status.Show();
        _status.Activate();
    }

    public void ShowRegister()
    {
        if (_register is null)
        {
            _register = new RegisterWindow { DataContext = new RegisterViewModel(Control!, a => Dispatcher.UIThread.Post(a)) };
            _register.Closed += (_, _) =>
            {
                (_register.DataContext as IDisposable)?.Dispose();
                _register = null;
            };
        }

        _register.Show();
        _register.Activate();
    }

    private bool TryCreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var menu = TrayMenuBuilder.Build(
                new TrayMenuViewModel(Control!, a => Dispatcher.UIThread.Post(a)), ShowStatus, ShowRegister, () => _ = Control!.ExitAsync());
            var icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://socalytics-manager/Assets/tray.png"))),
                ToolTipText = "SocAlytics Analyst Manager",
                Menu = menu,
            };
            _tray = icon;
            icon.IsVisible = true;
            return true;
        }
        catch (Exception)
        {
            _tray = null;
            return false;
        }
    }
}
