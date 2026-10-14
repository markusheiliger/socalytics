using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Cli;

public sealed class ManagerCliConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-cc-" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<(int Code, string Out, string Err)> RunAsync(FakeManagerControl control, params string[] args)
    {
        if (_server is null)
        {
            _server = LocalControlServer.Bind(SocketPath);
            _server.Start(control);
        }

        var o = new StringWriter();
        var e = new StringWriter();
        var code = await ManagerCli.RunAsync(args, SocketPath, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Concurrency_shows_and_sets_the_host_local_limit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl { Concurrency = new ConcurrencySnapshot(2, 4, null, 2) };
        var shown = await RunAsync(control, "concurrency");
        shown.Code.ShouldBe(0);
        shown.Out.ShouldContain("effective 2");
        shown.Out.ShouldContain("runtime maximum unknown");

        var set = await RunAsync(control, "concurrency", "3");
        set.Code.ShouldBe(0);
        control.Calls.ShouldContain("concurrency:3");
        set.Out.ShouldContain("host-local 3");
    }

    [Fact]
    public async Task Invalid_limits_are_refused_and_change_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl();
        foreach (var bad in new[] { "0", "-1", "abc" })
        {
            (await RunAsync(control, "concurrency", bad)).Code.ShouldNotBe(0);
        }

        control.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_lists_limit_and_active_attempts_without_secrets()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl { State = "active", Concurrency = new ConcurrencySnapshot(2, 4, 3, 2) };
        var id = Guid.NewGuid();
        control.ActiveAttempts.Add(new ActiveAttemptStatus(id, 1, "player-detection", "running", DateTimeOffset.UtcNow, null));

        var r = await RunAsync(control, "status");

        r.Code.ShouldBe(0);
        r.Out.ShouldContain("Concurrency: effective 2");
        r.Out.ShouldContain("Active attempts: 1");
        r.Out.ShouldContain(id.ToString());
        r.Out.ShouldContain("player-detection");
    }
}
