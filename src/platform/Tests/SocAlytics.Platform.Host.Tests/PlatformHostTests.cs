using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class PlatformHostTests
{
    private const string ApiResourceName = "api";
    private const string MigratorResourceName = "migrator";

    [Fact]
    public async Task AppHostStartsHealthyApiWithOperationalOpenApiSurface()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appHost = await CreateAppHostAsync(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("postgres", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("rustfs", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("nats", timeout.Token);
        await app.ResourceNotifications.WaitForResourceAsync("mailpit", KnownResourceStates.Running, timeout.Token);
        await app.ResourceNotifications.WaitForResourceAsync(
            MigratorResourceName, KnownResourceStates.Finished, timeout.Token);
        app.ResourceNotifications.TryGetCurrentState(MigratorResourceName, out var migrator).ShouldBeTrue();
        migrator!.Snapshot.ExitCode.ShouldBe(0);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(ApiResourceName, timeout.Token);

        var apiResource = appHost.CreateResourceBuilder<ProjectResource>(ApiResourceName).Resource;
#pragma warning disable CS0618 // the spec names this API for reading the resource environment
        var environment = await apiResource.GetEnvironmentVariableValuesAsync();
#pragma warning restore CS0618
        environment["Stamp__Id"].ShouldBe("local-dev-stamp");
        environment["Nats__Authentication__Mode"].ShouldBe("Jwt");
        environment["Nats__Authentication__OperatorSeed"].ShouldStartWith("SO");
        environment["Nats__Authentication__AccountSeed"].ShouldStartWith("SA");
        environment["Nats__Authentication__SystemAccountSeed"].ShouldStartWith("SA");
        environment["AnalystWork__QueueUrl"].ShouldStartWith("nats://");
        environment["ConnectionStrings__nats"].ShouldStartWith("nats://");
        Uri.TryCreate(environment["Stamp__PublicBaseUri"], UriKind.Absolute, out var stampBase).ShouldBeTrue();
        stampBase!.Scheme.ShouldBe("http");
        environment["Email__Smtp__Host"].ShouldNotBeNullOrWhiteSpace();
        int.Parse(environment["Email__Smtp__Port"]!).ShouldBeGreaterThan(0);
        environment["Email__Smtp__Security"].ShouldBe("StartTlsWhenAvailable");
        environment["Email__SenderAddress"].ShouldBe("socalytics@localhost");
        environment["Email__SenderName"].ShouldBe("SocAlytics");
        environment["Email__Confirmation__Lifetime"].ShouldBe("1.00:00:00");
        environment["Email__Confirmation__MaxPerHour"].ShouldBe("3");
        environment["Email__Confirmation__AttemptsPerMinute"].ShouldBe("10");
        environment["Email__Delivery__MaxAttempts"].ShouldBe("8");
        environment["Email__Delivery__PollInterval"].ShouldBe("00:00:05");
        environment["Email__Delivery__LeaseDuration"].ShouldBe("00:02:00");
        environment["Notifications__CoalescingWindow"].ShouldBe("00:15:00");
        Uri.TryCreate(environment["Notifications__WebUiBaseUri"], UriKind.Absolute, out var webBase).ShouldBeTrue();
        webBase!.Scheme.ShouldBe("http");

        using var client = app.CreateHttpClient(ApiResourceName, "http");
        await ShouldReturnSuccessAsync(client, "/alive", timeout.Token);
        using var healthResponse = await client.GetAsync("/health", timeout.Token);
        healthResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await healthResponse.Content.ReadAsStringAsync(timeout.Token)).Trim().ShouldBe("Healthy");

        using var openApiResponse = await client.GetAsync("/openapi/v1.json", timeout.Token);
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var openApiStream = await openApiResponse.Content.ReadAsStreamAsync(timeout.Token);
        using var openApiDocument = await JsonDocument.ParseAsync(openApiStream, cancellationToken: timeout.Token);
        openApiDocument.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("v1");
        openApiDocument.RootElement.GetProperty("paths").TryGetProperty("/api/v1/session", out _).ShouldBeTrue();

        var operationIds = openApiDocument.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(static path => path.Value.EnumerateObject())
            .Where(static operation => operation.Value.ValueKind == JsonValueKind.Object
                && operation.Value.TryGetProperty("operationId", out _))
            .Select(static operation => operation.Value.GetProperty("operationId").GetString())
            .ToHashSet();
        string[] analysisOperationIds =
        [
            "getAnalysisRun", "listMatchAnalysisRuns", "claimLogicalJob", "renewAttemptLease", "completeAttempt",
            "listOutboxMessages", "getOutboxMessage", "getAnalysisOperationalSummary", "listNotificationReceipts",
            "reconcileAnalysisPublication",
        ];
        foreach (var operationId in analysisOperationIds)
        {
            operationIds.ShouldContain(operationId);
        }
    }

    [Fact]
    public async Task AppHostComposesWebUiThatReferencesApi()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var appHost = await CreateAppHostAsync(timeout.Token);

        var web = appHost.Resources.Single(static resource => resource.Name == "web");
        var api = appHost.Resources.Single(static resource => resource.Name == ApiResourceName);
        web.Annotations.OfType<ResourceRelationshipAnnotation>()
            .ShouldContain(annotation => annotation.Resource == api && annotation.Type == "Reference");
    }

    [Fact]
    public async Task AppHostComposesMailpitRelayWithPinnedImageAndEndpoints()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var appHost = await CreateAppHostAsync(timeout.Token);

        var mailpit = appHost.Resources.Single(static resource => resource.Name == "mailpit");
        var image = mailpit.Annotations.OfType<ContainerImageAnnotation>().Single();
        image.Image.ShouldBe("axllent/mailpit");
        image.Tag.ShouldBe("v1.31.5");
        var endpoints = mailpit.Annotations.OfType<EndpointAnnotation>().ToDictionary(static e => e.Name);
        endpoints["smtp"].TargetPort.ShouldBe(1025);
        endpoints["http"].TargetPort.ShouldBe(8025);
    }

    [Fact]
    public async Task AppHostDoesNotStartApiWhenMigratorFails()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var appHost = await CreateAppHostAsync(timeout.Token);
        appHost.CreateResourceBuilder<ProjectResource>(MigratorResourceName)
            .WithEnvironment("Migrator__ScriptTimeout", "00:00:00");
        await using var app = await appHost.BuildAsync(timeout.Token);

        var apiStates = new List<string?>();
        var watcher = Task.Run(async () =>
        {
            await foreach (var evt in app.ResourceNotifications.WatchAsync(timeout.Token))
            {
                if (evt.Resource.Name == ApiResourceName)
                {
                    lock (apiStates)
                    {
                        apiStates.Add(evt.Snapshot.State?.Text);
                    }
                }
            }
        }, timeout.Token);

        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceAsync(
            MigratorResourceName, KnownResourceStates.Finished, timeout.Token);
        app.ResourceNotifications.TryGetCurrentState(MigratorResourceName, out var migrator).ShouldBeTrue();
        migrator!.Snapshot.ExitCode.ShouldBe(2);

        using var short30 = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        short30.CancelAfter(TimeSpan.FromSeconds(30));
        await app.ResourceNotifications.WaitForResourceAsync(
            ApiResourceName, KnownResourceStates.FailedToStart, short30.Token);

        lock (apiStates)
        {
            apiStates.ShouldNotContain(KnownResourceStates.Running);
        }

        timeout.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(watcher);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateAppHostAsync(CancellationToken cancellationToken) =>
        await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(
            ["--SocAlytics:LocalDatabase:Persistent=false", "--SocAlytics:ApiHttpsEndpoint=false"], cancellationToken);

    [Fact]
    public void ApplicationAndInfrastructureLayersContributeRegistrations()
    {
        IServiceCollection services = new ServiceCollection();

        ShouldAddOneRegistration(services, static collection => collection.AddApplication());

        services.AddInfrastructure().ShouldBeSameAs(services);
        services.ShouldContain(static descriptor =>
            descriptor.ServiceType.FullName == "SocAlytics.Platform.Infrastructure.Persistence.PlatformDataSource");

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(static r => r.Name == "database");
        registration.Tags.ShouldNotContain("live");
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
        services.Count.ShouldBeGreaterThan(initialCount);
    }
}