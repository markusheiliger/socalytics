using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SocAlytics.Platform.Api;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
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
    public void AllCapabilityCompositionBoundariesContributeRegistrations()
    {
        IServiceCollection services = new ServiceCollection();

        ShouldAddModuleRegistrations(services, static collection => collection.AddAgentOrchestrationModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddAnalysisModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddClubModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddIdentityAccessModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddRecordingsModule());
        ShouldAddModuleRegistrations(services, static collection => collection.AddRegistryModule());

        services.Count(descriptor => descriptor.ServiceType == typeof(IModuleMigrationContributor)).ShouldBe(6);
    }

    [Fact]
    public async Task MigrationFailureKeepsReadinessUnhealthyWithSanitizedDiagnostics()
    {
        const string sentinel = "diagnostic-sentinel";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistenceMigrationReadiness(
            $"Host=localhost;Username={sentinel};UnsupportedOption=value");
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        await using var provider = services.BuildServiceProvider();
        var migrationService = provider.GetServices<IHostedService>()
            .OfType<PersistenceMigrationStartupService>()
            .Single();
        await migrationService.StartAsync(TestContext.Current.CancellationToken);

        var healthChecks = provider.GetRequiredService<HealthCheckService>();
        var readiness = await healthChecks.CheckHealthAsync(
            check => !check.Tags.Contains("live"),
            TestContext.Current.CancellationToken);
        var liveness = await healthChecks.CheckHealthAsync(
            check => check.Tags.Contains("live"),
            TestContext.Current.CancellationToken);

        readiness.Status.ShouldBe(HealthStatus.Unhealthy);
        (readiness.Entries["database-migrations"].Description ?? string.Empty).ShouldNotContain(sentinel);
        liveness.Status.ShouldBe(HealthStatus.Healthy);
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
        var initialCount = services.Count;

        register(services).ShouldBeSameAs(services);
        services.Count.ShouldBe(initialCount + 2);
    }
}