using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class VerificationBaseUriTests(PostgresContainerFixture postgres)
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData("https://web.example", false, true)]
    [InlineData("https://web.example:8443", false, true)]
    [InlineData("https://web.example/", false, true)]
    [InlineData("http://localhost:5173", false, false)]
    [InlineData("http://localhost:5173", true, true)]
    [InlineData("https://web.example/ui", true, false)]
    [InlineData("https://web.example?x=1", true, false)]
    [InlineData("https://web.example#top", true, false)]
    [InlineData("https://web.example?", true, false)]
    [InlineData("https://user@web.example", true, false)]
    [InlineData("web.example", true, false)]
    [InlineData("/relative", true, false)]
    [InlineData("ftp://web.example", true, false)]
    [InlineData("", true, false)]
    public void ValidatesVerificationBaseUri(string? value, bool development, bool valid)
    {
        var options = new RegistryOptions();
        options.Pairing.VerificationBaseUri = value;
        new RegistryOptionsValidator(development).Validate(null, options).Succeeded.ShouldBe(valid);
    }

    [Theory]
    [InlineData(null, "https://stamp.example/analyst-managers/pair")]
    [InlineData("https://web.example", "https://web.example/analyst-managers/pair")]
    [InlineData("https://web.example/", "https://web.example/analyst-managers/pair")]
    public void VerificationUriUsesBaseWhenSet(string? baseUri, string expected)
    {
        var stamp = new StampOptions { Id = "s", PublicBaseUri = "https://stamp.example" };
        var registry = new RegistryOptions { Pairing = { VerificationBaseUri = baseUri } };
        stamp.VerificationUri(registry).ToString().ShouldBe(expected);
        stamp.TokenUri.ToString().ShouldBe("https://stamp.example/api/v1/analyst-managers/token");
    }

    [Fact]
    public async Task InvalidValueIsRefusedAtStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = new PlatformApiFactory(db, extraSettings: new Dictionary<string, string?>
        {
            ["Registry:Pairing:VerificationBaseUri"] = "https://web.example/with/path",
        });

        Should.Throw<Exception>(() => factory.CreateApiClient());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://web.example")]
    public async Task PairingResponseUsesTheConfiguredBase(string? baseUri)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var settings = new Dictionary<string, string?>();
        if (baseUri is not null)
        {
            settings["Registry:Pairing:VerificationBaseUri"] = baseUri;
        }

        await using var factory = new PlatformApiFactory(db, new Dictionary<string, string?>
        {
            ["ClubDisplayName"] = "Registry Club",
            ["FirstClubAdmin:AccountName"] = "registry-admin",
            ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
        }, extraSettings: settings);
        await factory.WaitUntilHealthyAsync(ct);
        using var manager = new TestAnalystManager();
        using var client = factory.CreateApiClient();

        using var response = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        var verification = root.GetProperty("verificationUri").GetString()!;
        var stampBase = factory.Services.GetRequiredService<IOptions<StampOptions>>().Value.PublicBaseUri.TrimEnd('/');
        verification.ShouldBe((baseUri?.TrimEnd('/') ?? stampBase) + "/analyst-managers/pair");
    }
}
