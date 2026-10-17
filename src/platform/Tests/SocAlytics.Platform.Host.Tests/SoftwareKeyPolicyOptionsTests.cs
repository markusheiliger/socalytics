using Microsoft.Extensions.Configuration;
using Shouldly;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Application.Registry;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class SoftwareKeyPolicyOptionsTests
{
    [Theory]
    [InlineData("refused", SoftwareKeyPolicy.Refused)]
    [InlineData("headless-server-only", SoftwareKeyPolicy.HeadlessServerOnly)]
    [InlineData("all", SoftwareKeyPolicy.All)]
    public void ConfigurationValuesBind(string value, SoftwareKeyPolicy expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Registry:SoftwareKeys"] = value })
            .Build();
        var options = new RegistryOptions();
        configuration.GetSection(RegistryOptions.SectionName).Bind(options);
        options.SoftwareKeys.ShouldBe(expected);
    }

    [Fact]
    public void DefaultIsRefused() => new RegistryOptions().SoftwareKeys.ShouldBe(SoftwareKeyPolicy.Refused);

    [Theory]
    [InlineData(SoftwareKeyPolicy.Refused, false, true)]
    [InlineData(SoftwareKeyPolicy.HeadlessServerOnly, false, true)]
    [InlineData(SoftwareKeyPolicy.All, false, false)]
    [InlineData(SoftwareKeyPolicy.All, true, true)]
    public void AllIsAcceptedOnlyInDevelopmentAndTest(SoftwareKeyPolicy policy, bool developmentOrTest, bool valid)
    {
        var options = new RegistryOptions { SoftwareKeys = policy };
        new RegistryOptionsValidator(developmentOrTest, developmentOrTest).Validate(null, options).Succeeded.ShouldBe(valid);
    }
}
