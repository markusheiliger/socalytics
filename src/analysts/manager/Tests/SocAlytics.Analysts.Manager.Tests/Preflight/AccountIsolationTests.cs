using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Preflight;

public sealed class AccountIsolationTests
{
    private const string Detail = "A rootful container runtime reached through group membership requires a dedicated non-login service account.";

    private static SimulatedRuntimeAdapter Adapter(string? access) =>
        new(new SimulatedAnalystOptions { DaemonAccess = access }, TimeProvider.System);

    private static PreflightRunner Runner(SimulatedRuntimeAdapter adapter, ApplicationProfile profile, string shell) =>
        new(new RuntimeAdapterCatalog([adapter]), SimulatedRuntimeAdapter.List(),
            Options.Create(new RuntimeOptions { Profile = SimulatedRuntimeAdapter.TestProfile }), new FakeTimeProvider(),
            profile, new AccountShell(() => shell));

    [Theory]
    [InlineData("rootful-group", "/bin/bash", false)]
    [InlineData("rootful-group", "/usr/sbin/nologin", true)]
    [InlineData("rootful-group", "/sbin/nologin", true)]
    [InlineData("rootful-group", "/bin/false", true)]
    [InlineData("rootful-group", "/usr/bin/false", true)]
    [InlineData("rootless", "/bin/bash", true)]
    [InlineData("rootless", "/usr/sbin/nologin", true)]
    [InlineData(null, "/bin/bash", true)]
    [InlineData(null, "/bin/false", true)]
    public async Task Headless_profile_follows_the_account_rule(string? access, string shell, bool passes)
    {
        var result = await Runner(Adapter(access), ApplicationProfile.HeadlessServer, shell)
            .RunAsync(TestContext.Current.CancellationToken);

        var check = result.Checks.Single(c => c.Id == "account-isolation");
        check.Passed.ShouldBe(passes);
        result.Passed.ShouldBe(passes);
        if (!passes)
        {
            check.Detail.ShouldBe(Detail);
            check.Remediation.ShouldBe("Run the Manager under a dedicated account without a login shell, or use a rootless runtime (see the Manager README, Headless Server Profile).");
        }
    }

    [Fact]
    public async Task Tray_profile_is_unaffected()
    {
        var result = await Runner(Adapter("rootful-group"), ApplicationProfile.Tray, "/bin/bash")
            .RunAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
        result.Checks.ShouldNotContain(c => c.Id == "account-isolation");
    }

    [Fact]
    public async Task Failed_check_makes_the_Manager_runtime_unavailable_and_admits_no_work()
    {
        var adapter = Adapter("rootful-group");
        await using var host = Host(adapter, ApplicationProfile.HeadlessServer, "/bin/bash");
        await Restore(host);
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);

        var status = host.Control.GetStatus();
        status.OperatingState.ShouldBe("runtime-unavailable");
        status.Preflight.ShouldBeOfType<PreflightResult>().Checks.Single(c => !c.Passed).Detail.ShouldBe(Detail);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
        host.Services.GetRequiredService<Diagnostics.StatusReportBuilder>().Build()!.OperatingState.ShouldBe("runtime-unavailable");
    }

    [Fact]
    public async Task No_login_account_admits_work()
    {
        await using var host = Host(Adapter("rootful-group"), ApplicationProfile.HeadlessServer, "/usr/sbin/nologin");
        await Restore(host);
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);

        host.Control.GetStatus().OperatingState.ShouldBe("running");
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeTrue();
    }

    [Fact]
    public async Task Tray_host_with_rootful_group_runs()
    {
        await using var host = Host(Adapter("rootful-group"), ApplicationProfile.Tray, "/bin/bash");
        await Restore(host);
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);

        host.Control.GetStatus().OperatingState.ShouldBe("running");
    }

    [Theory]
    [InlineData("/usr/sbin/nologin", true)]
    [InlineData("/sbin/nologin", true)]
    [InlineData("/bin/false", true)]
    [InlineData("/usr/bin/false", true)]
    [InlineData("/bin/bash", false)]
    [InlineData("/bin/zsh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NoLogin_shells_are_recognized(string? shell, bool noLogin) =>
        AccountShell.IsNoLogin(shell).ShouldBe(noLogin);

    [Fact]
    public void Unknown_shell_counts_as_login_capable()
    {
        new AccountShell(() => null).IsLoginCapable().ShouldBeTrue();
        new AccountShell(() => throw new IOException("x")).IsLoginCapable().ShouldBeTrue();
        new AccountShell(() => "/usr/sbin/nologin").IsLoginCapable().ShouldBeFalse();
    }

    [Fact]
    public void Passwd_parsing_matches_by_uid_then_by_name()
    {
        string[] lines =
        [
            "# comment:x:1000:1000:::/bin/bash",
            "root:x:0:0:root:/root:/bin/bash",
            "svc:x:998:998:Service:/var/lib/svc:/usr/sbin/nologin",
            "empty:x:997:997:::",
            "short:x:5",
        ];

        AccountShell.ParsePasswd(lines, 998, "other").ShouldBe("/usr/sbin/nologin");
        AccountShell.ParsePasswd(lines, 997, null).ShouldBe("/bin/sh");
        AccountShell.ParsePasswd(lines, 4242, "root").ShouldBe("/bin/bash");
        AccountShell.ParsePasswd(lines, 4242, "nobody").ShouldBeNull();
        AccountShell.ParsePasswd(lines, 4242, null).ShouldBeNull();
    }

    [Fact]
    public void Current_account_shell_is_read_on_unix()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads the real account database");

        new AccountShell().LoginShell().ShouldNotBeNullOrWhiteSpace();
    }

    private static ManagerTestHost Host(SimulatedRuntimeAdapter adapter, ApplicationProfile profile, string shell) =>
        new(profile: profile, configure: s =>
        {
            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog([adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(SimulatedRuntimeAdapter.List());
            s.Configure<RuntimeOptions>(o => o.Profile = SimulatedRuntimeAdapter.TestProfile);
            s.RemoveAll<AccountShell>();
            s.AddSingleton(new AccountShell(() => shell));
        });

    private static async Task Restore(ManagerTestHost host)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
    }
}
