using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class AgentToolRouteGroupTests
{
    private const string GroupPrefix = "/api/v1/agent-tools";
    private const string WorkloadPolicy = "AgentWorkload";

    private static readonly string[] MemberAndManagerPolicies = ["ActiveMember", "SessionHolder", "AnalystManager"];

    private static readonly string[] ForbiddenOperationWords =
    [
        "admin", "approve", "retire", "enable", "disable", "register", "create", "update", "delete", "purge", "cancel",
        "import", "issue", "claim", "lease", "checkpoint", "complete", "fail", "finalize", "submit", "start", "pair",
        "activate", "revoke", "lifecycle", "worker", "reconcile", "refresh", "rotate",
    ];

    [Fact]
    public async Task The_openapi_agent_tool_operations_equal_the_allowlist()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var actual = new List<(string OperationId, string Method, string Path)>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(p => p.Name.StartsWith(GroupPrefix, StringComparison.Ordinal)))
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                actual.Add((operation.Value.GetProperty("operationId").GetString()!, operation.Name.ToUpperInvariant(), path.Name));
            }
        }

        var expected = AgentToolAllowlist.Entries
            .Select(entry => (entry.OperationId, "POST", entry.PathTemplate))
            .ToArray();

        actual.OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(item => item.OperationId, StringComparer.Ordinal));
    }

    [Fact]
    public void Allowlisted_operations_are_never_administration_mutation_or_worker_operations()
    {
        foreach (var entry in AgentToolAllowlist.Entries)
        {
            ForbiddenOperationWords.ShouldNotContain(
                word => entry.OperationId.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"{entry.OperationId} must be a read-only evidence operation.");
        }
    }

    [Fact]
    public async Task The_group_has_only_allowlisted_endpoints_that_require_the_workload_policy()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var allowlisted = AgentToolAllowlist.Entries.ToDictionary(entry => entry.OperationId, StringComparer.Ordinal);
        var group = endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase) ?? false)
            .ToArray();

        group.Length.ShouldBe(allowlisted.Count);

        foreach (var endpoint in group)
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "?";
            var path = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var label = $"{string.Join(",", methods)} {path} ({name})";

            allowlisted.TryGetValue(name, out var entry).ShouldBeTrue($"{label} is not an allowlisted operation.");
            path.Replace("{attemptId:guid}", "{attemptId}", StringComparison.Ordinal).ShouldBe(entry!.PathTemplate);
            methods.ShouldBe(["POST"], $"{label} must be a POST.");

            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull($"{label} must not allow anonymous access.");
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .Where(policy => policy is not null)
                .ToArray();
            policies.ShouldContain(WorkloadPolicy, $"{label} must require {WorkloadPolicy}.");
            policies.ShouldNotContain(
                policy => MemberAndManagerPolicies.Contains(policy),
                $"{label} must not require a member or Analyst Manager policy.");
        }

        // No allowlisted operation identifier is shared with an endpoint outside the group.
        var outside = endpoints
            .Except(group)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null);
        outside.ShouldNotContain(name => allowlisted.ContainsKey(name!));
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:socalytics"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1",
                ["IdentityAccess:Session:IdleTimeout"] = "00:30:00",
                ["IdentityAccess:Session:AbsoluteLifetime"] = "08:00:00",
                ["IdentityAccess:Lockout:MaxFailedAccessAttempts"] = "5",
                ["IdentityAccess:Lockout:LockoutDuration"] = "00:15:00",
                ["IdentityAccess:Password:RequiredLength"] = "12",
                ["IdentityAccess:OneTimeCredential:Lifetime"] = "1.00:00:00",
                ["Email:Smtp:Host"] = "127.0.0.1",
                ["Email:Smtp:Port"] = "1",
                ["Email:Smtp:Security"] = "StartTlsWhenAvailable",
                ["Email:SenderAddress"] = "socalytics@localhost",
                ["Email:SenderName"] = "SocAlytics",
                ["Notifications:WebUiBaseUri"] = "http://localhost:5173",
                ["ObjectStorage:ServiceUrl"] = "http://127.0.0.1:9",
                ["ObjectStorage:AccessKey"] = "test-access",
                ["ObjectStorage:SecretKey"] = "test-secret",
                ["ObjectStorage:Bucket"] = "test-bucket",
                ["ObjectStorage:EnsureBucketOnStartup"] = "false",
                ["Recordings:Upload:SessionLifetime"] = "00:30:00",
                ["Recordings:Upload:GrantLifetime"] = "00:15:00",
                ["Recordings:Upload:ExpirySweepInterval"] = "00:01:00",
                ["Recordings:Upload:MaxObjectSizeBytes"] = "5497558138880",
                ["Recordings:Upload:MinPartSizeBytes"] = "5242880",
                ["Recordings:Upload:MaxPartSizeBytes"] = "5368709120",
                ["Recordings:Upload:MaxPartCount"] = "10000",
                ["Recordings:Upload:MaxGrantsPerRequest"] = "1000",
                ["Recordings:Upload:AllowedContentTypes:0"] = "video/mp4",
                ["Analysis:Results:GrantLifetime"] = "00:10:00",
                ["Analysis:Results:MaxObjectsPerAttempt"] = "16",
                ["Analysis:Results:MaxObjectBytes"] = "104857600",
                ["Analysis:Results:MaxAttemptBytes"] = "1073741824",
                ["Analysis:Results:MaxManifestBytes"] = "1048576",
                ["Analysis:Results:MaxFactArtifactBytes"] = "16777216",
                ["Analysis:Results:MaxFactPayloadBytes"] = "16384",
                ["Analysis:Results:MaxFactsPerResult"] = "50000",
                ["Analysis:Facts:DefaultPageSize"] = "100",
                ["Analysis:Facts:MaxPageSize"] = "500",
                ["Analysis:Reads:GrantLifetime"] = "00:10:00",
                ["Analysis:Reads:MaxPageSize"] = "100",
                ["Analysis:Evidence:GrantLifetime"] = "00:05:00",
                ["Analysis:Evidence:RetryAfter"] = "00:00:05",
                ["Analysis:Evidence:ResolutionsPerMinute"] = "60",
                ["Analysis:Evidence:MaxDuration"] = "00:10:00",
                ["Analysis:Evidence:MaxContext"] = "00:00:10",
            }));
            builder.ConfigureServices(services =>
            {
                var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType?.Name == "ClubBootstrapHostedService").ToArray();
                foreach (var descriptor in hosted)
                {
                    services.Remove(descriptor);
                }
            });
        });
}
