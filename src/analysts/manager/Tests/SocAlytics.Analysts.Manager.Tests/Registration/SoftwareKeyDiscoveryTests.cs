using Shouldly;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class SoftwareKeyDiscoveryTests
{
    private static readonly RegisterRequest Request = new("https://platform.test");

    public static TheoryData<ApplicationProfile, string[]?, bool> Cases => new()
    {
        { ApplicationProfile.Tray, [], false },
        { ApplicationProfile.HeadlessServer, [], false },
        { ApplicationProfile.Tray, ["headless-server"], false },
        { ApplicationProfile.HeadlessServer, ["headless-server"], true },
        { ApplicationProfile.Tray, ["tray", "headless-server"], true },
        { ApplicationProfile.HeadlessServer, ["tray", "headless-server"], true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SoftwareOnlyHostPairsOnlyWhenTheStampAcceptsItsProfile(
        ApplicationProfile profile, string[]? allowedFor, bool expectPairing)
    {
        await using var host = new ManagerTestHost(KeyProtection.Software, profile: profile);
        host.Platform.SoftwareKeysAllowedFor = allowedFor;

        var result = await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBe(expectPairing);
        if (expectPairing)
        {
            host.Platform.ClaimedKeyProtection.ShouldBe("software");
            return;
        }

        result.Code.ShouldBe(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
        result.Notice.ShouldNotBeNull().ShouldContain("hardware-backed key storage");
        if (profile == ApplicationProfile.HeadlessServer)
        {
            result.Notice.ShouldContain("headless-server-only");
        }

        host.Keys.CreatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(ApplicationProfile.Tray, true)]
    [InlineData(ApplicationProfile.HeadlessServer, true)]
    [InlineData(ApplicationProfile.Tray, false)]
    [InlineData(ApplicationProfile.HeadlessServer, false)]
    public async Task ResponseWithoutTheFieldFallsBackToSoftwareKeysAllowed(ApplicationProfile profile, bool allowed)
    {
        await using var host = new ManagerTestHost(KeyProtection.Software, profile: profile);
        host.Platform.SoftwareKeysAllowed = allowed;
        host.Platform.SoftwareKeysAllowedFor = null;

        var result = await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBe(allowed);
        if (!allowed)
        {
            result.Code.ShouldBe(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
        }
    }

    [Theory]
    [InlineData(ApplicationProfile.Tray)]
    [InlineData(ApplicationProfile.HeadlessServer)]
    public async Task HardwareBackedHostPairsWhateverTheStampAllows(ApplicationProfile profile)
    {
        await using var host = new ManagerTestHost(KeyProtection.Tpm, profile: profile);
        host.Platform.SoftwareKeysAllowedFor = [];

        (await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();
        host.Platform.ClaimedKeyProtection.ShouldBe("tpm");
    }
}
