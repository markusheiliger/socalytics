using System.Runtime.Versioning;
using SocAlytics.Analysts.Manager.Autostart;
using Shouldly;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Autostart;

public sealed class AutostartTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public void FormatsContainExecutableAndAutostartArgument()
    {
        AutostartEntryFormats.DesktopEntry("/opt/m/manager").ShouldContain("Exec=\"/opt/m/manager\" --autostart");
        AutostartEntryFormats.DesktopEntry("/opt/m/manager").ShouldContain("Type=Application");
        AutostartEntryFormats.RunValue(@"C:\M\m.exe").ShouldBe("\"C:\\M\\m.exe\" --autostart");
        var plist = AutostartEntryFormats.LaunchAgentPlist("/Applications/M & Co/m");
        plist.ShouldContain("<key>RunAtLoad</key>");
        plist.ShouldContain("<string>Aqua</string>");
        plist.ShouldContain("M &amp; Co");
        plist.ShouldContain("--autostart");
    }

    [Fact]
    public void HiddenDesktopEntryIsDisabled()
    {
        AutostartEntryFormats.DesktopEntryEnabled("[Desktop Entry]\nHidden=true\n").ShouldBeFalse();
        AutostartEntryFormats.DesktopEntryEnabled("[Desktop Entry]\nType=Application\n").ShouldBeTrue();
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(AutostartTests))]
    [SupportedOSPlatform("linux")]
    public async Task XdgEnableReadBackDisable()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "autostart-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            var registrar = new XdgAutostartRegistrar();
            (await registrar.IsEnabledAsync(ct)).ShouldBeFalse();
            await registrar.EnableAsync("/opt/m/manager", ct);
            File.Exists(Path.Combine(dir, "autostart", "socalytics-analyst-manager.desktop")).ShouldBeTrue();
            (await registrar.IsEnabledAsync(ct)).ShouldBeTrue();
            await registrar.DisableAsync(ct);
            (await registrar.IsEnabledAsync(ct)).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
