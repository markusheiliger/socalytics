using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class PlatformHostTests
{
    private const string ApiResourceName = "api";

    [Fact]
    public async Task AppHostStartsApiWithLivenessOpenApiSurfaceAndMigrationGatedReadiness()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceAsync(ApiResourceName, KnownResourceStates.Running, timeout.Token);

        using var client = app.CreateHttpClient(ApiResourceName);
        await ShouldReturnSuccessAsync(client, "/alive", timeout.Token);

        // No database is composed yet, so migrations cannot complete and readiness stays unavailable.
        using var healthResponse = await client.GetAsync("/health", timeout.Token);
        healthResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await healthResponse.Content.ReadAsStringAsync(timeout.Token)).ShouldBe("Unhealthy");

        using var openApiResponse = await client.GetAsync("/openapi/v1.json", timeout.Token);
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var openApiStream = await openApiResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var openApiDocument = await JsonDocument.ParseAsync(openApiStream, cancellationToken: timeout.Token);
        openApiDocument.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("v1");
        openApiDocument.RootElement.GetProperty("paths").EnumerateObject().Count().ShouldBe(0);
    }

    [Fact]
    public void AllCapabilityCompositionBoundariesContributeRegistrations()
    {
        IServiceCollection services = new ServiceCollection();

        ShouldAddModuleRegistrations(services, static collection => collection.AddAgentOrchestrationModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddAnalysisModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddClubModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddIdentityAccessModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddRecordingsModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddRegistryModule());

        services.Count.ShouldBe(18);
    }

    private static async Task ShouldReturnSuccessAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private static void ShouldAddModuleRegistrations(
        IServiceCollection services,
        Func<IServiceCollection, IServiceCollection> register)
    {
        // Marker, migration contributor, and role-scoped connection factory.
        var initialCount = services.Count;

        register(services).ShouldBeSameAs(services);
        services.Count.ShouldBe(initialCount + 3);
    }
}