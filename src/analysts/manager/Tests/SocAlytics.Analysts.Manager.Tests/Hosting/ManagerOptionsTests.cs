using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Hosting;

public class ManagerOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
        return new ServiceCollection().AddManagerCore(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Defaults_match_the_documented_non_production_values()
    {
        using var sp = Build();

        sp.GetRequiredService<IOptions<ManagerOptions>>().Value.StateDirectory.ShouldBeNull();

        var pkcs11 = sp.GetRequiredService<IOptions<Pkcs11Options>>().Value;
        pkcs11.ModulePath.ShouldBe("/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so");
        pkcs11.TokenLabel.ShouldBe("socalytics-device");

        var platform = sp.GetRequiredService<IOptions<PlatformOptions>>().Value;
        platform.StatusCheckInterval.ShouldBe(TimeSpan.FromSeconds(60));
        platform.RetryBackoff.Initial.ShouldBe(TimeSpan.FromSeconds(2));
        platform.RetryBackoff.Max.ShouldBe(TimeSpan.FromSeconds(60));

        var operating = sp.GetRequiredService<IOptions<OperatingOptions>>().Value;
        operating.DrainTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        operating.DrainTimeoutPolicy.ShouldBe(DrainTimeoutPolicy.CancelRequest);
        operating.CleanupBound.ShouldBe(TimeSpan.FromSeconds(30));
        operating.SessionEndDrainBound.ShouldBe(TimeSpan.FromSeconds(5));
        operating.MaxTimedPause.ShouldBe(TimeSpan.FromDays(7));

        var runtime = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
        runtime.Profile.ShouldBeNull();
        runtime.PreflightInterval.ShouldBe(TimeSpan.FromSeconds(60));
        runtime.ProbeImage.ShouldBeNull();
    }

    [Fact]
    public void Configuration_overrides_defaults()
    {
        using var sp = Build(new()
        {
            ["Operating:DrainTimeoutPolicy"] = "StopAndCleanUp",
            ["Platform:RetryBackoff:Initial"] = "00:00:05",
            ["Manager:StateDirectory"] = "/tmp/x",
        });

        sp.GetRequiredService<IOptions<OperatingOptions>>().Value.DrainTimeoutPolicy.ShouldBe(DrainTimeoutPolicy.StopAndCleanUp);
        sp.GetRequiredService<IOptions<PlatformOptions>>().Value.RetryBackoff.Initial.ShouldBe(TimeSpan.FromSeconds(5));
        sp.GetRequiredService<IOptions<ManagerOptions>>().Value.StateDirectory.ShouldBe("/tmp/x");
    }

    [Fact]
    public void Linux_state_directory_uses_xdg_state_home_when_set()
    {
        ManagerStatePaths.ResolveLinux("/xdg", "/home/u")
            .ShouldBe(Path.Combine("/xdg", "socalytics", "analyst-manager"));
    }

    [Fact]
    public void Linux_state_directory_falls_back_to_local_state()
    {
        ManagerStatePaths.ResolveLinux(null, "/home/u")
            .ShouldBe(Path.Combine("/home/u", ".local", "state", "socalytics", "analyst-manager"));
        ManagerStatePaths.ResolveLinux("", "/home/u")
            .ShouldBe(Path.Combine("/home/u", ".local", "state", "socalytics", "analyst-manager"));
    }

    [Fact]
    public void Configured_state_directory_wins()
    {
        ManagerStatePaths.Resolve("/custom").ShouldBe("/custom");
    }
}
