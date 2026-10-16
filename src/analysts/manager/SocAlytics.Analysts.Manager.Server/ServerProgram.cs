using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalControl;

namespace SocAlytics.Analysts.Manager.Server;

/// <summary>Everything the headless executable takes from its surroundings; tests replace it.</summary>
internal sealed class ServerProgramEnvironment
{
    public Func<bool> IsLinux { get; init; } = OperatingSystem.IsLinux;

    public Func<string> SocketPath { get; init; } = LocalControlPaths.SocketPath;

    public TextWriter Output { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    /// <summary>Acts like a SIGTERM when cancelled; null registers the process signal.</summary>
    public CancellationToken? Terminate { get; init; }

    public Action<HostApplicationBuilder>? ConfigureHost { get; init; }

    /// <summary>Called once the host and the local-control server are running.</summary>
    public Action<IManagerControl>? Started { get; init; }
}

internal static class ServerProgram
{
    public const string UnsupportedMessage =
        "The headless server profile is available only on Linux; use socalytics-manager (tray profile).";

    public static async Task<int> RunAsync(string[] args, ServerProgramEnvironment environment)
    {
        if (!environment.IsLinux())
        {
            await environment.Error.WriteLineAsync(UnsupportedMessage);
            return ManagerCli.Refused;
        }

        var socketPath = environment.SocketPath();
        if (ManagerCli.IsCommand(args))
        {
            return await ManagerCli.RunAsync(args, socketPath, environment.Output, environment.Error);
        }

        using var instance = await SingleInstanceGuard.AcquireAsync(socketPath);
        if (instance is null)
        {
            return 0;
        }

        var builder = ManagerHost.CreateBuilder(args, ApplicationProfile.HeadlessServer);
        // Errors go to standard error, everything else to standard output; the journal captures both.
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Error);
        environment.ConfigureHost?.Invoke(builder);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var control = host.Services.GetRequiredService<IManagerControl>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            control.ExitCompleted += (_, _) => done.TrySetResult();
            instance.Start(control);

            using var sigterm = environment.Terminate is null
                ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    _ = control.SessionEndingAsync();
                })
                : null;
            using var terminate = environment.Terminate?.Register(() => _ = control.SessionEndingAsync());

            environment.Started?.Invoke(control);
            await done.Task;
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
