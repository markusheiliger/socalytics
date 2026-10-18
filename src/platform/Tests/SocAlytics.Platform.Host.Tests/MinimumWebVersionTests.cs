using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Api.WebUi;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class MinimumWebVersionTests
{
    private const string HeaderName = "SocAlytics-Minimum-Web-Version";

    private static readonly Dictionary<string, string?> RequiredSettings = new()
    {
        ["IdentityAccess:Session:IdleTimeout"] = "00:30:00",
        ["IdentityAccess:Session:AbsoluteLifetime"] = "08:00:00",
        ["IdentityAccess:Lockout:MaxFailedAccessAttempts"] = "5",
        ["IdentityAccess:Lockout:LockoutDuration"] = "00:15:00",
        ["IdentityAccess:Password:RequiredLength"] = "12",
        ["IdentityAccess:OneTimeCredential:Lifetime"] = "1.00:00:00",
        ["Stamp:Id"] = "local-dev-stamp",
        ["Stamp:PublicBaseUri"] = "https://localhost",
        ["ObjectStorage:ServiceUrl"] = "http://127.0.0.1:9",
        ["ObjectStorage:AccessKey"] = "test-access",
        ["ObjectStorage:SecretKey"] = "test-secret",
        ["ObjectStorage:Bucket"] = "test-bucket",
        ["ObjectStorage:EnsureBucketOnStartup"] = "false",
        ["Recordings:Upload:SessionLifetime"] = "00:30:00",
        ["Recordings:Upload:GrantLifetime"] = "00:15:00",
        ["Recordings:Upload:ExpirySweepInterval"] = "00:01:00",
        ["Recordings:Upload:MaxObjectSizeBytes"] = "5497558138880",
        ["Recordings:Upload:MinPartSizeBytes"] = "5242880",
        ["Recordings:Upload:MaxPartSizeBytes"] = "5368709120",
        ["Recordings:Upload:MaxPartCount"] = "10000",
        ["Recordings:Upload:MaxGrantsPerRequest"] = "1000",
        ["Recordings:Upload:AllowedContentTypes:0"] = "video/mp4",
        ["Recordings:Upload:AllowedContentTypes:1"] = "video/quicktime",
        ["Analysis:Results:GrantLifetime"] = "00:10:00",
        ["Analysis:Results:MaxObjectsPerAttempt"] = "16",
        ["Analysis:Results:MaxObjectBytes"] = "104857600",
        ["Analysis:Results:MaxAttemptBytes"] = "1073741824",
        ["Analysis:Results:MaxManifestBytes"] = "1048576",
        ["Analysis:Results:MaxFactArtifactBytes"] = "16777216",
        ["Analysis:Results:MaxFactPayloadBytes"] = "16384",
        ["Analysis:Results:MaxFactsPerResult"] = "50000",
        ["Analysis:Facts:DefaultPageSize"] = "100",
        ["Analysis:Facts:MaxPageSize"] = "500",
        ["Analysis:Reads:GrantLifetime"] = "00:10:00",
        ["Analysis:Reads:MaxPageSize"] = "100",
    };

    private static WebApplicationFactory<WebUiOptions> CreateFactory(string? minimumVersion) =>
        new WebApplicationFactory<WebUiOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>(RequiredSettings)
                {
                    ["ConnectionStrings:socalytics"] = "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=1",
                };
                if (minimumVersion is not null)
                {
                    settings["WebUi:MinimumVersion"] = minimumVersion;
                }

                configuration.AddInMemoryCollection(settings);
            });
        });

    [Theory]
    [InlineData("/api/v1/session")]
    [InlineData("/api/v1/does-not-exist")]
    public async Task HeaderIsSentOnApiResponsesIncludingErrorsWhenSet(string path)
    {
        using var factory = CreateFactory("2026.1015.195544");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        response.Headers.GetValues(HeaderName).ShouldBe(["2026.1015.195544"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task HeaderIsAbsentWhenNoMinimumIsConfigured(string? minimumVersion)
    {
        using var factory = CreateFactory(minimumVersion);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/session", TestContext.Current.CancellationToken);

        response.Headers.Contains(HeaderName).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    public async Task HeaderIsAbsentOutsideTheApi(string path)
    {
        using var factory = CreateFactory("2026.1015.195544");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.Contains(HeaderName).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2026-1015-195544")]
    [InlineData("2026.1015.19554")]
    [InlineData("latest")]
    public void InvalidMinimumVersionFailsStartup(string minimumVersion)
    {
        using var factory = CreateFactory(minimumVersion);

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Message.ShouldContain("WebUi:MinimumVersion");
    }

    [Fact]
    public async Task RequestsAreNeverRefusedBecauseOfTheMinimum()
    {
        using var factory = CreateFactory("9999.1231.235959");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("SocAlytics-Web-Version", "2000.0101.000000");

        using var withMinimum = await client.GetAsync("/api/v1/session", TestContext.Current.CancellationToken);
        using var unrelated = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        withMinimum.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unrelated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
