using System.Security;

namespace SocAlytics.Analysts.Manager.Autostart;

public static class AutostartEntryFormats
{
    public const string AutostartArgument = "--autostart";
    public const string DesktopFileName = "socalytics-analyst-manager.desktop";
    public const string RunValueName = "SocAlytics.AnalystManager";
    public const string SystemdUnitName = "socalytics-analyst-manager.service";
    public const string MacBundleId = "com.socalytics.analyst-manager";

    public static string RunValue(string executablePath) => $"\"{executablePath}\" {AutostartArgument}";

    public static string DesktopEntry(string executablePath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=SocAlytics Analyst Manager\n" +
        $"Exec=\"{executablePath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" {AutostartArgument}\n" +
        "Terminal=false\n";

    public static string LaunchAgentPlist(string executablePath) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n<dict>\n" +
        $"  <key>Label</key>\n  <string>{MacBundleId}</string>\n" +
        "  <key>ProgramArguments</key>\n  <array>\n" +
        $"    <string>{SecurityElement.Escape(executablePath)}</string>\n    <string>{AutostartArgument}</string>\n  </array>\n" +
        "  <key>RunAtLoad</key>\n  <true/>\n" +
        "  <key>LimitLoadToSessionType</key>\n  <string>Aqua</string>\n" +
        "</dict>\n</plist>\n";

    public static string SystemdUserUnit(string executablePath, TimeSpan drainTimeout) =>
        "[Unit]\n" +
        "Description=socAlytics Analyst Manager (headless server profile)\n" +
        "After=network-online.target\n" +
        "\n[Service]\n" +
        "Type=exec\n" +
        $"ExecStart={SystemdExecPath(executablePath)}\n" +
        "Restart=on-failure\n" +
        "RestartSec=10s\n" +
        "KillSignal=SIGTERM\n" +
        $"TimeoutStopSec={(long)Math.Ceiling((drainTimeout + TimeSpan.FromSeconds(30)).TotalSeconds)}s\n" +
        "\n[Install]\n" +
        "WantedBy=default.target\n";

    private static string SystemdExecPath(string executablePath) =>
        executablePath.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' or '%' or '$')
            ? "\"" + executablePath.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\""
            : executablePath;

    public static bool DesktopEntryEnabled(string content) =>
        !content.Split('\n').Any(l => l.Trim().Equals("Hidden=true", StringComparison.OrdinalIgnoreCase));
}
