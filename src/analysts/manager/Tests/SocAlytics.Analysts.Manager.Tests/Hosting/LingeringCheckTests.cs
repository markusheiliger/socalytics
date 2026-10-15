using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Hosting;

public sealed class LingeringCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa-linger-" + Guid.NewGuid().ToString("N")[..8]);

    public LingeringCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_root, true);
    }

    private IHostedService Service(ApplicationProfile profile, StringWriter error) =>
        new LingeringWarningService(profile, new LingeringCheck(_root, "analyst"), NullLogger<LingeringWarningService>.Instance, error);

    [Fact]
    public void Marker_present_is_on()
    {
        File.WriteAllText(Path.Combine(_root, "analyst"), string.Empty);

        new LingeringCheck(_root, "analyst").Evaluate().ShouldBe(LingeringState.On);
    }

    [Fact]
    public void Marker_missing_is_off()
    {
        File.WriteAllText(Path.Combine(_root, "someone-else"), string.Empty);

        new LingeringCheck(_root, "analyst").Evaluate().ShouldBe(LingeringState.Off);
    }

    [Fact]
    public void Unreadable_directory_is_unknown()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName != "root", "Needs permission enforcement");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_root, UnixFileMode.None);
        }

        new LingeringCheck(_root, "analyst").Evaluate().ShouldBe(LingeringState.Unknown);
    }

    [Theory]
    [InlineData(ApplicationProfile.HeadlessServer, true)]
    [InlineData(ApplicationProfile.Tray, false)]
    public async Task Warning_is_written_only_in_the_headless_profile_when_off(ApplicationProfile profile, bool warns)
    {
        var error = new StringWriter();

        await Service(profile, error).StartAsync(CancellationToken.None);

        if (warns)
        {
            error.ToString().ShouldContain("sudo loginctl enable-linger analyst");
        }
        else
        {
            error.ToString().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task No_warning_when_lingering_is_on()
    {
        File.WriteAllText(Path.Combine(_root, "analyst"), string.Empty);
        var error = new StringWriter();

        await Service(ApplicationProfile.HeadlessServer, error).StartAsync(CancellationToken.None);

        error.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Manager_keeps_running_when_lingering_is_off()
    {
        var start = Service(ApplicationProfile.HeadlessServer, new StringWriter()).StartAsync(CancellationToken.None);

        (await Task.WhenAny(start, Task.Delay(5000, TestContext.Current.CancellationToken))).ShouldBe(start);
        start.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
