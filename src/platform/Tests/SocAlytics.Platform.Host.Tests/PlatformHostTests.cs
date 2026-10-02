using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    private const string DatabaseResourceName = "platform";

    [Fact]
    public async Task AppHostMigratesPostgreSqlAndServesHealthyApiAcrossRepeatStartup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Restarting the API re-runs migrations against the already-migrated database.
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(DatabaseResourceName, timeout.Token);
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

        var connectionString = await app.GetConnectionStringAsync(DatabaseResourceName, timeout.Token);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "select count(*) from information_schema.schemata where schema_name = 'socalytics_migrations'", connection);
            ((long)(await command.ExecuteScalarAsync(timeout.Token))!).ShouldBe(1);
        }

        await app.ResourceCommands.ExecuteCommandAsync(ApiResourceName, KnownResourceCommands.RestartCommand, timeout.Token);
        using var restartedClient = app.CreateHttpClient(ApiResourceName);
        await WaitForHealthyEndpointAsync(restartedClient, "/health", timeout.Token);
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

    }

    // Polls the endpoint directly; resource health notifications can be stale or missed across a restart.
    private static async Task WaitForHealthyEndpointAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                // The proxy can accept connections before the restarted API listens, so bound each attempt.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await client.GetAsync(path, attempt.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
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

        var registeredCount = services.Count;
        register(services).ShouldBeSameAs(services);
        services.Count.ShouldBe(registeredCount);
    }
}