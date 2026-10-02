using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Api;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class MigrationReadinessTests
{
    private const string UnreachableConnection =
        "Host=127.0.0.1;Port=1;Database=secretdb;Username=secretuser;Password=secretpass;Timeout=2;Command Timeout=2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadinessStaysUnavailableWhenMigrationsCannotComplete()
    {
        await using var app = await StartAsync(UnreachableConnection);
        using var client = CreateClient(app);

        (await client.GetAsync("/alive", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var health = await client.GetAsync("/health", Ct);
        health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await health.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("secretpass");
        body.ShouldNotContain("secretuser");
        body.ShouldNotContain("127.0.0.1");
    }

    [Fact]
    public async Task ReadinessStaysUnavailableWhenDatabaseIsNotConfigured()
    {
        await using var app = await StartAsync(null);
        using var client = CreateClient(app);

        (await client.GetAsync("/alive", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task OpenApiSurfaceExposesNoDomainRoutes()
    {
        await using var app = await StartAsync(UnreachableConnection);
        using var client = CreateClient(app);

        using var response = await client.GetAsync("/openapi/v1.json", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("paths").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task ApiRegistersAllSixModuleMigrationContributors()
    {
        await using var app = await StartAsync(UnreachableConnection);

        app.Services.GetServices<IMigrationContributor>()
            .Select(contributor => contributor.Module)
            .ShouldBe(PersistenceModuleKey.All, ignoreOrder: true);
    }

    private static async Task<WebApplication> StartAsync(string? connectionString)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PlatformApiComposition.ConnectionName}"] = connectionString,
        });
        builder.AddPlatformApi();

        var app = builder.Build();
        app.MapPlatformApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
