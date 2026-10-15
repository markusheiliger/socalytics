using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Cli;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Server;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Server;

public sealed class ServerProgramTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-srv-" + Guid.NewGuid().ToString("N")[..8]);
    private LocalControlServer? _server;

    private string SocketPath => Path.Combine(_dir, "run", "manager.sock");

    public void Dispose()
    {
        _server?.Dispose();
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private ServerProgramEnvironment Environment(StringWriter output, StringWriter error, bool linux = true, CancellationToken? terminate = null,
        Action<Microsoft.Extensions.Hosting.HostApplicationBuilder>? configureHost = null) => new()
    {
        IsLinux = () => linux,
        SocketPath = () => SocketPath,
        Output = output,
        Error = error,
        Terminate = terminate,
        ConfigureHost = configureHost,
    };

    private void StartFake(FakeManagerControl control)
    {
        _server = LocalControlServer.Bind(SocketPath);
        _server.Start(control);
    }

    [Fact]
    public void Project_and_assembly_closure_contain_no_avalonia()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SocAlytics.Analysts.Manager.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        root.ShouldNotBeNull();
        File.ReadAllText(Path.Combine(root, "SocAlytics.Analysts.Manager.Server", "SocAlytics.Analysts.Manager.Server.csproj"))
            .ShouldNotContain("Avalonia");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>();
        pending.Push(typeof(ServerProgram).Assembly);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                reference.Name.ShouldNotStartWith("Avalonia");
                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Refuses_outside_linux()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["status"] })
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await ServerProgram.RunAsync(args, Environment(o, e, linux: false));
            code.ShouldBe(1);
            e.ToString().Trim().ShouldBe("The headless server profile is available only on Linux; use socalytics-manager (tray profile).");
        }
    }

    [Fact]
    public async Task Command_is_forwarded_to_running_instance()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl { State = "active" };
        StartFake(control);
        var o = new StringWriter();
        var code = await ServerProgram.RunAsync(["pause"], Environment(o, new StringWriter()));
        code.ShouldBe(0);
        control.Calls.ShouldContain("pause");
    }

    [Fact]
    public async Task Second_start_hands_over_and_exits()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl();
        var shown = new TaskCompletionSource();
        control.ShowStatusRequested += (_, _) => shown.TrySetResult();
        StartFake(control);
        var code = await ServerProgram.RunAsync([], Environment(new StringWriter(), new StringWriter())).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        code.ShouldBe(0);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Commands_give_the_same_results_as_the_shared_cli()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var control = new FakeManagerControl { State = "active" };
        StartFake(control);
        foreach (var args in new[]
        {
            new[] { "pause" }, new[] { "pause", "--for", "30" }, new[] { "resume" }, new[] { "concurrency", "2" },
            new[] { "concurrency" }, new[] { "status" }, new[] { "status", "--json" }, new[] { "exit" },
        })
        {
            var so = new StringWriter();
            var se = new StringWriter();
            var server = await ServerProgram.RunAsync(args, Environment(so, se));
            var to = new StringWriter();
            var te = new StringWriter();
            var tray = await ManagerCli.RunAsync(args, SocketPath, to, te);
            server.ShouldBe(tray, string.Join(' ', args));
            so.ToString().ShouldBe(to.ToString(), string.Join(' ', args));
            se.ToString().ShouldBe(te.ToString(), string.Join(' ', args));
        }
    }

    [Fact]
    public async Task Headless_host_runs_serves_commands_and_stops_on_termination()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        using var terminate = new CancellationTokenSource();
        var o = new StringWriter();
        var e = new StringWriter();
        var run = ServerProgram.RunAsync([], Environment(o, e, terminate: terminate.Token, configureHost: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Manager:StateDirectory"] = Path.Combine(_dir, "state") });
            builder.Services.RemoveAll<IDeviceKeyProvider>();
            builder.Services.AddSingleton<IDeviceKeyProvider>(new InMemoryDeviceKeyProvider(KeyProtection.Tpm));
        }));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        (int Code, string Out, string Err) status;
        do
        {
            var so = new StringWriter();
            var se = new StringWriter();
            status = (await ServerProgram.RunAsync(["status", "--json"], Environment(so, se)), so.ToString(), se.ToString());
        }
        while (status.Code != 0 && DateTime.UtcNow < deadline && !run.IsCompleted);

        status.Code.ShouldBe(0, status.Err);
        status.Out.ShouldContain("\"kind\":\"status\"");

        terminate.Cancel();
        (await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}
