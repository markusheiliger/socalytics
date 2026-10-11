using System.Text.Json;
using Json.Schema;
using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-cli-" + Guid.NewGuid().ToString("N")[..8]);
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

    private void Start(FakeManagerControl control)
    {
        _server = LocalControlServer.Bind(SocketPath);
        _server.Start(control);
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await ManagerCli.RunAsync(args, SocketPath, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Commands_succeed_with_exit_code_0()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl { State = "active" };
        Start(control);
        foreach (var args in new[]
        {
            new[] { "status" }, new[] { "pause" }, new[] { "pause", "--for", "30" }, new[] { "pause", "--until", "2030-01-01T00:00:00Z" },
            new[] { "resume" }, new[] { "exit" }, new[] { "unregister" }, new[] { "unregister", "--discard-local" },
            new[] { "autostart" }, new[] { "autostart", "on" },
        })
        {
            (await RunAsync(args)).Code.ShouldBe(0, string.Join(' ', args));
        }
    }

    [Fact]
    public async Task Register_prints_pairing_code()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        Start(new FakeManagerControl());
        var r = await RunAsync("register", "--platform", "https://platform.example", "--stamp-id", "stamp-1", "--host-label", "h");
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("ABCD-1234");
    }

    [Fact]
    public async Task Status_json_validates_against_schema()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        Start(new FakeManagerControl { State = "active" });
        var r = await RunAsync("status", "--json");
        r.Code.ShouldBe(0);
        var schema = LocalControlSchema.Value;
        using var doc = JsonDocument.Parse(r.Out);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe("status");
        var envelope = JsonSerializer.SerializeToElement(new { protocolVersion = 1, ok = true, result = doc.RootElement });
        schema.Evaluate(envelope).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Usage_errors_exit_2()
    {
        (await RunAsync("bogus")).Code.ShouldBe(2);
        (await RunAsync("register")).Code.ShouldBe(2);
        (await RunAsync("pause", "--for", "1", "--until", "2030-01-01T00:00:00Z")).Code.ShouldBe(2);
    }

    [Fact]
    public async Task Refusal_exits_1_and_prints_code()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        Start(new FakeManagerControl { RefuseResume = true });
        var r = await RunAsync("resume");
        r.Code.ShouldBe(1);
        r.Err.ShouldContain("invalid-registration-state");
    }

    [Fact]
    public async Task Missing_manager_exits_3()
    {
        (await RunAsync("status")).Code.ShouldBe(3);
    }

    [Fact]
    public async Task Missing_platform_is_usage_error()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        Start(new FakeManagerControl());
        var r = await RunAsync("register");
        r.Code.ShouldBe(2);
        var bad = await RunAsync("unregister", "--discard-local");
        bad.Code.ShouldBe(0);
    }
}
