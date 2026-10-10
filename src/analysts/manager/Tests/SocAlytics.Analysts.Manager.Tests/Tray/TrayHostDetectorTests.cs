using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Tray;

namespace SocAlytics.Analysts.Manager.Tests.Tray;

public class TrayHostDetectorTests
{
    [Fact]
    public void Probe_result_decides_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            new TrayHostDetector(() => false).IsTrayAvailable().ShouldBeTrue();
            return;
        }

        new TrayHostDetector(() => true).IsTrayAvailable().ShouldBeTrue();
        new TrayHostDetector(() => false).IsTrayAvailable().ShouldBeFalse();
        new TrayHostDetector(() => throw new InvalidOperationException()).IsTrayAvailable().ShouldBeFalse();
    }
}
