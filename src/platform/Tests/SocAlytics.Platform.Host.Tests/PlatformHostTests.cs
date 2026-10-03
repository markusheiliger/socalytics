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
    private const string PostgresResourceName = "postgres";
    private const string DatabaseResourceName = "platform";

    [Fact]
    public async Task AppHostComposesPostgreSqlMigratesAndServesHealthyApiAcrossRepeatStartup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);

        // PostgreSQL server and database are healthy before the API is allowed to start.
        await app.ResourceNotifications.WaitForResourceHealthyAsync(PostgresResourceName, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(DatabaseResourceName, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);

        using var client = app.CreateHttpClient(ApiResourceName);
        await ShouldServeCompleteSurfaceAsync(client, timeout.Token);

        // A second host startup against the already migrated database must also reach readiness.
        await app.ResourceCommands.ExecuteCommandAsync(ApiResourceName, KnownResourceCommands.RestartCommand, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);

        using var restartedClient = app.CreateHttpClient(ApiResourceName);
        await ShouldServeCompleteSurfaceAsync(restartedClient, timeout.Token);
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

    private static async Task ShouldServeCompleteSurfaceAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await ShouldReturnSuccessAsync(client, "/alive", cancellationToken);

        using var healthResponse = await client.GetAsync("/health", cancellationToken);
        healthResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await healthResponse.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("Healthy");

        using var openApiResponse = await client.GetAsync("/openapi/v1.json", cancellationToken);
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var openApiStream = await openApiResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var openApiDocument = await JsonDocument.ParseAsync(openApiStream, cancellationToken: cancellationToken);
        openApiDocument.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("v1");
        openApiDocument.RootElement.GetProperty("paths").EnumerateObject().Count().ShouldBe(0);
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