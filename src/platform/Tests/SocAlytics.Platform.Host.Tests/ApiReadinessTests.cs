extern alias PlatformApi;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class ApiReadinessTests
{
    private const string Secret = "do-not-leak-secret";

    [Fact]
    public async Task ReadinessStaysUnavailableWhenMigrationsFailAndDiagnosticsAreSanitized()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var factory = CreateFactory(
            $"Host=127.0.0.1;Port=1;Database=unreachable;Username=u;Password={Secret};Timeout=2;Command Timeout=2");
        using var client = factory.CreateClient();

        await WaitForMigrationAttemptAsync(client, timeout.Token);

        using var health = await client.GetAsync("/health", timeout.Token);
        health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await health.Content.ReadAsStringAsync(timeout.Token)).ShouldNotContain(Secret);

        using var alive = await client.GetAsync("/alive", timeout.Token);
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadinessStaysUnavailableWithoutDatabaseConfigurationAndNoDomainRouteExists()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var factory = CreateFactory(null);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health", timeout.Token);
        health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var alive = await client.GetAsync("/alive", timeout.Token);
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var openApi = await client.GetAsync("/openapi/v1.json", timeout.Token);
        openApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var stream = await openApi.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        document.RootElement.GetProperty("paths").EnumerateObject().Count().ShouldBe(0);

        using var domain = await client.GetAsync("/clubs", timeout.Token);
        domain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task WaitForMigrationAttemptAsync(HttpClient client, CancellationToken cancellationToken)
    {
        // The unreachable database fails fast; /health must stay unhealthy before and after the attempt.
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        using var response = await client.GetAsync("/health", cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private static WebApplicationFactory<PlatformApi::Program> CreateFactory(string? connectionString) =>
        new WebApplicationFactory<PlatformApi::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:platform", connectionString ?? string.Empty);
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", string.Empty);
        });
}
