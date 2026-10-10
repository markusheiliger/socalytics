using System.Text;

namespace SocAlytics.Analysts.Manager.LocalControl;

public static class LocalControlPaths
{
    private const int MacMaxPathBytes = 104;

    public static string SocketPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocAlytics", "AnalystManager", "run", "manager.sock");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ResolveMac(home, Environment.GetEnvironmentVariable("TMPDIR"));
        }

        return ResolveLinux(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), home);
    }

    public static string ResolveLinux(string? xdgRuntimeDir, string home) =>
        string.IsNullOrWhiteSpace(xdgRuntimeDir)
            ? Path.Combine(home, ".local", "state", "socalytics", "run", "manager.sock")
            : Path.Combine(xdgRuntimeDir, "socalytics", "manager.sock");

    public static string ResolveMac(string home, string? tmpDir)
    {
        var preferred = Path.Combine(home, "Library", "Application Support", "SocAlytics", "AnalystManager", "run", "manager.sock");
        if (Encoding.UTF8.GetByteCount(preferred) <= MacMaxPathBytes)
        {
            return preferred;
        }

        return Path.Combine(string.IsNullOrWhiteSpace(tmpDir) ? "/tmp" : tmpDir, "socalytics", "manager.sock");
    }
}
