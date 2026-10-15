using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class PairingProfileClaimTests
{
    [Theory]
    [InlineData(ApplicationProfile.Tray, "tray")]
    [InlineData(ApplicationProfile.HeadlessServer, "headless-server")]
    public async Task Pairing_request_claims_the_host_profile(ApplicationProfile profile, string expected)
    {
        await using var host = new ManagerTestHost(profile: profile);

        var result = await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        host.Platform.ClaimedApplicationProfile.ShouldBe(expected);
    }
}
