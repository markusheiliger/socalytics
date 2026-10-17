using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class EmailOptionsTests
{
    private static WebApplicationFactory<Program> CreateFactory(string environment, string security) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(TestIdentityAccessSettings.Values);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Smtp:Security"] = security });
            });
        });

    [Fact]
    public void ProductionStartupRefusesOpportunisticTlsNamingTheSetting()
    {
        using var factory = CreateFactory("Production", "StartTlsWhenAvailable");

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Message.ShouldContain("Email:Smtp:Security");
    }

    [Fact]
    public void ProductionStartupAcceptsStartTls()
    {
        using var factory = CreateFactory("Production", "StartTls");

        Should.NotThrow(() => factory.CreateClient().Dispose());
    }
}
