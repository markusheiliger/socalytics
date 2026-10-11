using System.Diagnostics;

namespace SocAlytics.Analysts.Manager.Tray;

public sealed class TrayHostDetector(Func<bool>? linuxProbe = null)
{
    private readonly Func<bool> _linuxProbe = linuxProbe ?? ProbeStatusNotifierWatcher;

    public bool IsTrayAvailable()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return true;
        }

        try
        {
            return _linuxProbe();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ProbeStatusNotifierWatcher()
    {
        var info = new ProcessStartInfo("dbus-send")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher",
        })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit(2000))
        {
            process.Kill(true);
            return false;
        }

        return process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("boolean true", StringComparison.Ordinal);
    }
}
