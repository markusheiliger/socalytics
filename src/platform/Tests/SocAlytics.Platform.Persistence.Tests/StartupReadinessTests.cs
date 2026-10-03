using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class StartupReadinessTests
{
    private const string Secret = "s3cr3t-pa55";

    [Fact]
    public async Task Readiness_is_unhealthy_with_a_sanitized_diagnostic_when_migrations_cannot_run()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformPersistence(o =>
            o.ConnectionString = $"Host=127.0.0.1;Port=1;Database=x;Username=u;Password={Secret};Timeout=2;Command Timeout=2");
        services.AddPlatformPersistenceStartup();
        await using var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Status.ShouldBe(HealthStatus.Unhealthy);
        var entry = report.Entries["migrations"];
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        (entry.Description ?? string.Empty).ShouldNotContain(Secret);
        (entry.Description ?? string.Empty).ShouldNotContain("127.0.0.1");
    }

    [Fact]
    public async Task Readiness_is_unhealthy_before_migrations_have_completed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformPersistence(o => o.ConnectionString = "Host=127.0.0.1;Port=1;Database=x");
        services.AddPlatformPersistenceStartup();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Status.ShouldBe(HealthStatus.Unhealthy);
    }
}
