using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Hosting;

public sealed class ApplicationProfileTests
{
    [Theory]
    [InlineData(ApplicationProfile.Tray, "tray")]
    [InlineData(ApplicationProfile.HeadlessServer, "headless-server")]
    public void ApiNamesAreStable(ApplicationProfile profile, string apiName) =>
        profile.ToApiName().ShouldBe(apiName);

    [Theory]
    [InlineData(ApplicationProfile.Tray)]
    [InlineData(ApplicationProfile.HeadlessServer)]
    public void HostResolvesTheProfileItWasBuiltWith(ApplicationProfile profile)
    {
        using var host = ManagerHost.CreateBuilder([], profile).Build();

        host.Services.GetRequiredService<ApplicationProfile>().ShouldBe(profile);
    }

    [Fact]
    public void ConfigurationCannotOverrideTheProfile()
    {
        using var host = ManagerHost.CreateBuilder(
            ["--ApplicationProfile=HeadlessServer", "--Profile=headless-server", "--Manager:ApplicationProfile=HeadlessServer"],
            ApplicationProfile.Tray).Build();

        host.Services.GetRequiredService<ApplicationProfile>().ShouldBe(ApplicationProfile.Tray);
    }
}
