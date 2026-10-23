using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Registry;

namespace SocAlytics.Platform.Images.Tests.Support;

/// <summary>
/// Arranges the default analysis workflow of a stamp with the existing configured-workflow seeding, run in-process in the Test
/// environment because the API container runs as Production, where the seeding is disabled.
/// </summary>
public static class DefaultWorkflowArrangement
{
    /// <param name="applicationConnectionString">A connection string of the application role (<c>socalytics_app</c>) of the stamp database.</param>
    public static async Task SeedAsync(string applicationConnectionString, CancellationToken ct)
    {
        var settings = Path.Combine(ImageBuildFixture.LocateRepositoryRoot(), "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(settings, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:socalytics"] = applicationConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();
        services.AddHealthChecks();
        services.AddScoped<IRequestContext, SeedRequestContext>();
        services.AddInfrastructure();
        services.AddApplication();
        await using var provider = services.BuildServiceProvider();

        foreach (var seeder in provider.GetServices<IHostedService>().OfType<ConfiguredWorkflowSeeder>())
        {
            await seeder.StartAsync(ct);
            await seeder.StopAsync(ct);
        }

        var resolution = await provider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct);
        resolution.ShouldBeOfType<WorkflowResolution.ResolvedResult>("the default analysis workflow must resolve after seeding");
    }

    private sealed class SeedRequestContext : IRequestContext
    {
        public Guid? MemberAccountId => null;

        public Guid? SessionId => null;

        public string CorrelationId => "image-evidence-seed";

        public AuditActorKind ActorKind => AuditActorKind.System;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "SocAlytics.Platform.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
