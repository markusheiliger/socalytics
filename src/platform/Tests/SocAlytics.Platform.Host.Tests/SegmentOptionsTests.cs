using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Api.WebUi;
using SocAlytics.Platform.Application.Segments;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class SegmentHostOptionsTests
{
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
        ["Analysis:Evidence:GrantLifetime"] = "00:05:00",
        ["Analysis:Evidence:RetryAfter"] = "00:00:05",
        ["Analysis:Evidence:ResolutionsPerMinute"] = "60",
        ["Analysis:Evidence:MaxDuration"] = "00:10:00",
        ["Analysis:Evidence:MaxContext"] = "00:00:10",
    };

    private static WebApplicationFactory<WebUiOptions> CreateFactory(params (string Key, string Value)[] overrides) =>
        new WebApplicationFactory<WebUiOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>(RequiredSettings)
                {
                    ["ConnectionStrings:socalytics"] = "Host=127.0.0.1;Port=9;Database=none;Username=none;******;Timeout=1",
                };
                foreach (var (key, value) in overrides)
                {
                    settings[key] = value;
                }

                configuration.AddInMemoryCollection(settings);
            });
        });

    [Fact]
    public async Task DevelopmentValuesBindAndTheStoreIsRegistered()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var materialization = factory.Services.GetRequiredService<IOptions<SegmentMaterializationOptions>>().Value;
        materialization.MaxConcurrentPerInstance.ShouldBe(2);
        materialization.MaxAttempts.ShouldBe(3);
        factory.Services.GetRequiredService<IOptions<SegmentToolchainOptions>>().Value
            .ExpectedVersionPrefix.ShouldNotBeNullOrWhiteSpace();

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISegmentStore>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Segments:Materialization:MaxAttempts", "0", "Segments:Materialization:MaxAttempts must be at least 1.")]
    [InlineData("Segments:Materialization:MaxConcurrentPerInstance", "0", "Segments:Materialization:MaxConcurrentPerInstance must be at least 1.")]
    [InlineData("Segments:Toolchain:FfmpegPath", " ", "Segments:Toolchain:FfmpegPath must not be empty.")]
    public void InvalidValueFailsStartup(string key, string value, string message)
    {
        using var factory = CreateFactory((key, value));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Message.ShouldContain(message);
    }
}
