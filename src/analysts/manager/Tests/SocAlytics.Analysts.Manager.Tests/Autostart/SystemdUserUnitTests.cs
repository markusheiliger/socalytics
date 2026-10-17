using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Analysts.Manager.Autostart;
using SocAlytics.Analysts.Manager.Hosting;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Autostart;

[Collection("XdgConfigHome")]
public sealed class SystemdUserUnitTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public void UnitTextIsExact()
    {
        AutostartEntryFormats.SystemdUserUnit("/opt/m/socalytics-manager-server", TimeSpan.FromMinutes(5)).ShouldBe(
            "[Unit]\n" +
            "Description=socAlytics Analyst Manager (headless server profile)\n" +
            "After=network-online.target\n" +
            "\n[Service]\n" +
            "Type=exec\n" +
            "ExecStart=/opt/m/socalytics-manager-server\n" +
            "Restart=on-failure\n" +
            "RestartSec=10s\n" +
            "KillSignal=SIGTERM\n" +
            "TimeoutStopSec=330s\n" +
            "\n[Install]\n" +
            "WantedBy=default.target\n");
    }

    [Fact]
    public void UnitQuotesPathsWithSpaces() =>
        AutostartEntryFormats.SystemdUserUnit("/opt/my m/server", TimeSpan.Zero)
            .ShouldContain("ExecStart=\"/opt/my m/server\"\n");

    private sealed class FakeRunner(params int[] exitCodes) : IProcessRunner
    {
        private int _next;

        public List<string> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(fileName + " " + string.Join(' ', arguments));
            var code = _next < exitCodes.Length ? exitCodes[_next] : 0;
            _next++;
            return Task.FromResult(new ProcessRunResult(code, code == 0 ? "" : "Failed to connect to bus"));
        }
    }

    private static async Task WithConfigHome(string? value, Func<string?, Task> body)
    {
        var old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value);
        try
        {
            await body(value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "systemd-" + Guid.NewGuid().ToString("N"));

    [SupportedOSPlatform("linux")]
    private static SystemdUserUnitRegistrar Create(FakeRunner runner) =>
        new(runner, Options.Create(new OperatingOptions { DrainTimeout = TimeSpan.FromMinutes(1) }));

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(SystemdUserUnitTests))]
    [SupportedOSPlatform("linux")]
    public async Task UnitFileIsUnderXdgConfigHome()
    {
        var dir = TempDir();
        await WithConfigHome(dir, _ =>
        {
            SystemdUserUnitRegistrar.UnitPath().ShouldBe(Path.Combine(dir, "systemd", "user", "socalytics-analyst-manager.service"));
            return Task.CompletedTask;
        });
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(SystemdUserUnitTests))]
    [SupportedOSPlatform("linux")]
    public async Task UnitFileDefaultsToHomeConfig() =>
        await WithConfigHome(null, _ =>
        {
            SystemdUserUnitRegistrar.UnitPath().ShouldBe(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "socalytics-analyst-manager.service"));
            return Task.CompletedTask;
        });

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(SystemdUserUnitTests))]
    [SupportedOSPlatform("linux")]
    public async Task OnOffAndStateRunSystemctl()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = TempDir();
        try
        {
            await WithConfigHome(dir, async _ =>
            {
                var runner = new FakeRunner();
                var registrar = Create(runner);
                (await registrar.IsEnabledAsync(ct)).ShouldBeFalse();
                runner.Calls.ShouldBeEmpty();

                await registrar.EnableAsync("/opt/m/server", ct);
                var path = SystemdUserUnitRegistrar.UnitPath();
                File.ReadAllText(path).ShouldBe(AutostartEntryFormats.SystemdUserUnit("/opt/m/server", TimeSpan.FromMinutes(1)));
                runner.Calls.ShouldBe([
                    "systemctl --user daemon-reload",
                    "systemctl --user enable socalytics-analyst-manager.service"]);

                runner.Calls.Clear();
                (await registrar.IsEnabledAsync(ct)).ShouldBeTrue();
                runner.Calls.ShouldBe(["systemctl --user is-enabled socalytics-analyst-manager.service"]);

                runner.Calls.Clear();
                await registrar.DisableAsync(ct);
                File.Exists(path).ShouldBeFalse();
                runner.Calls[0].ShouldBe("systemctl --user disable socalytics-analyst-manager.service");

                var disabled = Create(new FakeRunner(1));
                await File.WriteAllTextAsync(path, "x", ct);
                (await disabled.IsEnabledAsync(ct)).ShouldBeFalse();
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(SystemdUserUnitTests))]
    [SupportedOSPlatform("linux")]
    public async Task SystemctlFailureIsReportedAndFileStays()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = TempDir();
        try
        {
            await WithConfigHome(dir, async _ =>
            {
                var registrar = Create(new FakeRunner(0, 1));
                var e = await Should.ThrowAsync<AutostartException>(() => registrar.EnableAsync("/opt/m/server", ct));
                e.Message.ShouldContain("Failed to connect to bus");
                File.Exists(SystemdUserUnitRegistrar.UnitPath()).ShouldBeTrue();
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Theory(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(SystemdUserUnitTests))]
    [InlineData(ApplicationProfile.Tray, typeof(XdgAutostartRegistrar))]
    [InlineData(ApplicationProfile.HeadlessServer, typeof(SystemdUserUnitRegistrar))]
    public void ProfileSelectsTheRegistrar(ApplicationProfile profile, Type expected)
    {
        using var host = ManagerHost.CreateBuilder([], profile).Build();

        host.Services.GetRequiredService<IAutostartRegistrar>().ShouldBeOfType(expected);
    }
}
