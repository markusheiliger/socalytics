using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.Autostart;

public sealed record ProcessRunResult(int ExitCode, string Output);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var text = (await output + await error).Trim();
        return new ProcessRunResult(process.ExitCode, text);
    }
}

/// <summary>Headless server profile autostart: a systemd user unit enabled through <c>systemctl --user</c>.</summary>
[SupportedOSPlatform("linux")]
public sealed class SystemdUserUnitRegistrar(IProcessRunner runner, IOptions<OperatingOptions> operating) : IAutostartRegistrar
{
    private const string Systemctl = "systemctl";

    internal static string UnitPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "systemd", "user", AutostartEntryFormats.SystemdUnitName);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(UnitPath()))
            {
                return false;
            }

            var result = await RunSystemctlAsync(["--user", "is-enabled", AutostartEntryFormats.SystemdUnitName], cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart read failed", e);
        }
    }

    public async Task EnableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = UnitPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path, AutostartEntryFormats.SystemdUserUnit(executablePath, operating.Value.DrainTimeout), cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart write failed", e);
        }

        await RequireSuccessAsync(["--user", "daemon-reload"], cancellationToken);
        await RequireSuccessAsync(["--user", "enable", AutostartEntryFormats.SystemdUnitName], cancellationToken);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await RequireSuccessAsync(["--user", "disable", AutostartEntryFormats.SystemdUnitName], cancellationToken);
        try
        {
            File.Delete(UnitPath());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart remove failed", e);
        }

        await RequireSuccessAsync(["--user", "daemon-reload"], cancellationToken);
    }

    private async Task RequireSuccessAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await RunSystemctlAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.Output) ? $"exit code {result.ExitCode}" : result.Output;
            throw new AutostartException($"systemctl {string.Join(' ', arguments)} failed: {message}");
        }
    }

    private async Task<ProcessRunResult> RunSystemctlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(Systemctl, arguments, cancellationToken);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            throw new AutostartException($"systemctl failed: {e.Message}", e);
        }
    }
}
