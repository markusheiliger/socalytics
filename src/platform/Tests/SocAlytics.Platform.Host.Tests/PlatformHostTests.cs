using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class PlatformHostTests
{
    private const string ApiResourceName = "api";

    [Fact]
    public async Task AppHostStartsHealthyApiWithOperationalOpenApiSurface()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);

        using var client = app.CreateHttpClient(ApiResourceName);
        await ShouldReturnSuccessAsync(client, "/alive", timeout.Token);
        await ShouldReturnSuccessAsync(client, "/health", timeout.Token);

        using var openApiResponse = await client.GetAsync("/openapi/v1.json", timeout.Token);
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var openApiStream = await openApiResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var openApiDocument = await JsonDocument.ParseAsync(openApiStream, cancellationToken: timeout.Token);
        openApiDocument.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("v1");
        openApiDocument.RootElement.GetProperty("paths").EnumerateObject().Count().ShouldBe(0);
    }

    [Fact]
    public void ApplicationAndInfrastructureLayersContributeRegistrations()
    {
        IServiceCollection services = new ServiceCollection();

        ShouldAddOneRegistration(services, static collection => collection.AddApplication());

        services.AddInfrastructure().ShouldBeSameAs(services);
        services.ShouldContain(static descriptor =>
            descriptor.ServiceType.FullName == "SocAlytics.Platform.Infrastructure.Persistence.PlatformDataSource");
    }

    private static async Task ShouldReturnSuccessAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private static void ShouldAddOneRegistration(
        IServiceCollection services,
        Func<IServiceCollection, IServiceCollection> register)
    {
        var initialCount = services.Count;

        register(services).ShouldBeSameAs(services);
        services.Count.ShouldBe(initialCount + 1);
    }
}