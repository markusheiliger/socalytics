using System.Text.Json;
using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class StatusProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-sp-" + Guid.NewGuid().ToString("N")[..8]);
    private LocalControlServer? _server;

    private string SocketPath => Path.Combine(_dir, "run", "manager.sock");

    public void Dispose()
    {
        _server?.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private async Task<(int Code, string Out)> StatusAsync(FakeManagerControl control, bool json)
    {
        _server = LocalControlServer.Bind(SocketPath);
        _server.Start(control);
        var o = new StringWriter();
        var code = await ManagerCli.RunAsync(json ? ["status", "--json"] : ["status"], SocketPath, o, new StringWriter());
        return (code, o.ToString());
    }

    private static FakeManagerControl Control(string profile, string? lingering)
    {
        var control = new FakeManagerControl { State = "active", OperatingState = "running", PauseRemaining = 90, ApplicationProfile = profile, Lingering = lingering };
        control.ActiveAttempts.Add(new(Guid.NewGuid(), 2, "detect", "running", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return control;
    }

    [Theory]
    [InlineData("tray", null)]
    [InlineData("headless-server", "on")]
    [InlineData("headless-server", "off")]
    [InlineData("headless-server", "unknown")]
    public async Task Json_status_carries_the_profile_and_validates(string profile, string? lingering)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");

        var (code, text) = await StatusAsync(Control(profile, lingering), json: true);

        code.ShouldBe(0);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        root.GetProperty("applicationProfile").GetString().ShouldBe(profile);
        root.GetProperty("managerVersion").GetString().ShouldNotBeNullOrEmpty();
        if (lingering is null)
        {
            root.TryGetProperty("lingering", out _).ShouldBeFalse();
        }
        else
        {
            root.GetProperty("lingering").GetString().ShouldBe(lingering);
        }

        var envelope = JsonSerializer.SerializeToElement(new { protocolVersion = 1, ok = true, result = root });
        LocalControlSchema.Value.Evaluate(envelope).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Text_status_lists_every_FR003_field_and_the_tray_profile()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");

        var (_, text) = await StatusAsync(Control("tray", null), json: false);

        foreach (var expected in new[]
        {
            "Stamp: stamp-1", "Club:", "Connectivity: unknown", "Operating: running", "Pause remaining: 90s", "Concurrency: effective",
            "Active attempts: 1", "last heartbeat", "Registration: active", "Version: 0.1.0", "Profile: tray",
        })
        {
            text.ShouldContain(expected);
        }

        text.ShouldNotContain("Lingering:");
        text.ShouldNotContain("enable-linger");
    }

    [Theory]
    [InlineData("on", false)]
    [InlineData("unknown", false)]
    [InlineData("off", true)]
    public async Task Text_status_shows_lingering_and_the_hint_only_when_off(string lingering, bool hint)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");

        var (_, text) = await StatusAsync(Control("headless-server", lingering), json: false);

        text.ShouldContain("Profile: headless-server");
        text.ShouldContain($"Lingering: {lingering}");
        text.Contains("Run 'sudo loginctl enable-linger <user>' so the Manager keeps running without a session.").ShouldBe(hint);
    }
}
