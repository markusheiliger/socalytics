using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var host = ManagerHost.CreateBuilder(args).Build();
        host.Start();
        try
        {
            var control = host.Services.GetRequiredService<IManagerControl>();
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                _ = control.SessionEndingAsync();
            });
            // --autostart starts like any start; the tray-only behavior is in App.
            return BuildAvaloniaApp(control).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IManagerControl control) =>
        AppBuilder.Configure(() => new App { Control = control, Autostarted = Environment.GetCommandLineArgs().Contains("--autostart") }).UsePlatformDetect();
}
