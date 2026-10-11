namespace SocAlytics.Analysts.Manager.Hosting;

public static class ManagerStatePaths
{
    public static string Resolve(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SocAlytics",
                "AnalystManager");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "SocAlytics", "AnalystManager");
        }

        return ResolveLinux(Environment.GetEnvironmentVariable("XDG_STATE_HOME"), home);
    }

    public static string ResolveLinux(string? xdgStateHome, string home)
    {
        var root = string.IsNullOrWhiteSpace(xdgStateHome)
            ? Path.Combine(home, ".local", "state")
            : xdgStateHome;
        return Path.Combine(root, "socalytics", "analyst-manager");
    }
}
