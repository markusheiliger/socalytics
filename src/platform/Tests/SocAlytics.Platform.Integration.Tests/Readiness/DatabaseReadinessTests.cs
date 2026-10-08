using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Readiness;

public sealed class DatabaseReadinessTests(PostgresContainerFixture postgres)
{
    internal static WebApplicationFactory<Program> CreateFactory(string? connectionString, CapturingLoggerProvider capture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                var settings = new Dictionary<string, string?>(TestIdentityAccessSettings.Values);
                settings["ClubBootstrap:ClubDisplayName"] = "Readiness Club";
                settings["ClubBootstrap:FirstClubAdmin:AccountName"] = "readiness-admin";
                settings["ClubBootstrap:FirstClubAdmin:InitialPassword"] = "Readiness-Initial-Pass-1234";
                if (connectionString is not null)
                {
                    settings["ConnectionStrings:socalytics"] = connectionString;
                }

                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(capture));
        });

    internal static async Task ShouldBecomeHealthyAsync(HttpClient client, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode status;
        do
        {
            status = (await client.GetAsync("/health", ct)).StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(200, ct);
            }
        }
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        status.ShouldBe(HttpStatusCode.OK);
    }

    internal static async Task<string?> DescriptionAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "database", ct);
        return report.Entries["database"].Description;
    }

    [Fact]
    public async Task HealthIsHealthyAfterMigratorAppliedPlatformCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        await using var factory = CreateFactory(db.AppConnectionString, new CapturingLoggerProvider());
        using var client = factory.CreateClient();

        await ShouldBecomeHealthyAsync(client, ct);
    }

    [Fact]
    public async Task HealthIsUnavailableWithoutHistoryAndApiNeverCreatesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        await using var factory = CreateFactory(db.AppConnectionString, new CapturingLoggerProvider());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldStartWith("migration-state-not-current");

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT to_regclass('socalytics_migrations.history') IS NULL", connection);
        ((bool)(await command.ExecuteScalarAsync(ct))!).ShouldBeTrue();
    }

    [Fact]
    public async Task HealthIsUnavailableWhenOnlyFirstPlatformMigrationIsApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(
            db,
            () => MigrationCatalog.Create(MigrationCatalog.Platform.Scripts.Where(script => script.Sequence == 1)),
            ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);

        await using var factory = CreateFactory(db.AppConnectionString, new CapturingLoggerProvider());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldStartWith("migration-state-not-current");
    }

    [Fact]
    public async Task MissingConnectionStringReportsConfigurationWhileAliveStaysUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory(null, new CapturingLoggerProvider());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldBe("configuration");
        (await client.GetAsync("/alive", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var openApi = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", ct));
        openApi.RootElement.GetProperty("paths").EnumerateObject().ShouldBeEmpty();
    }
}
