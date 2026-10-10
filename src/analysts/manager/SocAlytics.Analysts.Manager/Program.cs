using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalControl;

namespace SocAlytics.Analysts.Manager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ManagerCli.IsCommand(args))
        {
            ManagerCli.AttachParentConsole();
            return ManagerCli.RunAsync(args, LocalControlPaths.SocketPath(), Console.Out, Console.Error).GetAwaiter().GetResult();
        }

        using var instance = SingleInstanceGuard.AcquireAsync(LocalControlPaths.SocketPath()).GetAwaiter().GetResult();
        if (instance is null)
        {
            return 0;
        }

        using var host = ManagerHost.CreateBuilder(args).Build();
        host.Start();
        try
        {
            var control = host.Services.GetRequiredService<IManagerControl>();
            instance.Start(control);
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
