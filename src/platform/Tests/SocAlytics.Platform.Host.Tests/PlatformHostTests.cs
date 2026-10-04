using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
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
    private const string RepeatApiResourceName = "api-repeat";
    private const string DatabaseResourceName = "platform";

    private static readonly string[] ExpectedSchemas = ["socalytics_migrations", "club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"];

    [Fact]
    public async Task AppHostComposesMigratedPostgreSqlAndOperationalOpenApiSurface()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(DatabaseResourceName, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);

        using var client = app.CreateHttpClient(ApiResourceName);
        await ShouldReturnSuccessAsync(client, "/alive", timeout.Token);
        await WaitForReadinessAsync(client, timeout.Token);

        using var openApiResponse = await client.GetAsync("/openapi/v1.json", timeout.Token);
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var openApiStream = await openApiResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var openApiDocument = await JsonDocument.ParseAsync(openApiStream, cancellationToken: timeout.Token);
        openApiDocument.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("v1");
        openApiDocument.RootElement.GetProperty("paths").EnumerateObject().Count().ShouldBe(0);

        var connectionString = await app.GetConnectionStringAsync(DatabaseResourceName, timeout.Token);
        connectionString.ShouldNotBeNullOrWhiteSpace();
        await ShouldHaveMigratedSchemasAsync(connectionString!, timeout.Token);
    }

    [Fact]
    public async Task SecondExplicitlyStartedApiRepeatsStartupAgainstMigratedDatabase()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        var database = appHost.Resources.OfType<PostgresDatabaseResource>().Single(r => r.Name == DatabaseResourceName);
        appHost.AddProject<Projects.SocAlytics_Platform_Api>(RepeatApiResourceName)
            .WithReference(appHost.CreateResourceBuilder(database))
            .WaitFor(appHost.CreateResourceBuilder(database))
            .WithHttpEndpoint()
            .WithHttpHealthCheck("/alive")
            .WithExplicitStart();
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);
        using var first = app.CreateHttpClient(ApiResourceName);
        await WaitForReadinessAsync(first, timeout.Token);

        await app.ResourceCommands.ExecuteCommandAsync(RepeatApiResourceName, KnownResourceCommands.StartCommand, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(RepeatApiResourceName, timeout.Token);
        using var second = app.CreateHttpClient(RepeatApiResourceName);
        await WaitForReadinessAsync(second, timeout.Token);
        await ShouldReturnSuccessAsync(second, "/alive", timeout.Token);
    }

    [Fact]
    public void AllCapabilityCompositionBoundariesContributeRegistrations()
    {
        IServiceCollection services = new ServiceCollection();

        ShouldAddRegistrations(services, static collection => collection.AddAgentOrchestrationModule());
        ShouldAddRegistrations(services, static collection => collection.AddAnalysisModule());
        ShouldAddRegistrations(services, static collection => collection.AddClubModule());
        ShouldAddRegistrations(services, static collection => collection.AddIdentityAccessModule());
        ShouldAddRegistrations(services, static collection => collection.AddRecordingsModule());
        ShouldAddRegistrations(services, static collection => collection.AddRegistryModule());

        services.Count.ShouldBeGreaterThanOrEqualTo(6);
    }

    private static async Task WaitForReadinessAsync(HttpClient client, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task ShouldHaveMigratedSchemasAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select count(*) from information_schema.schemata where schema_name = any(@names)", connection);
        command.Parameters.AddWithValue("names", ExpectedSchemas);

        ((long)(await command.ExecuteScalarAsync(cancellationToken))!).ShouldBe(ExpectedSchemas.Length);
    }

    private static async Task ShouldReturnSuccessAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private static void ShouldAddRegistrations(
        IServiceCollection services,
        Func<IServiceCollection, IServiceCollection> register)
    {
        var initialCount = services.Count;

        register(services).ShouldBeSameAs(services);
        services.Count.ShouldBeGreaterThan(initialCount);
    }
}