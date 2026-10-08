using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class EndpointSecurityTests
{
    private const string ApiPrefix = "/api/v1";
    private const string SessionScheme = "SocAlyticsSession";

    private static readonly HashSet<string> AnonymousOperations = new(StringComparer.Ordinal) { "signIn", "redeemCredential" };

    private static readonly HashSet<string> SessionHolderOperations =
        new(StringComparer.Ordinal) { "getSession", "signOut", "getCurrentMember", "changeOwnPassword" };

    private static readonly string[] ForbiddenPathSegments = ["bootstrap", "setup", "recovery"];

    [Fact]
    public async Task Every_api_endpoint_is_explicitly_classified()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsApiPath(endpoint.RoutePattern))
            .ToArray();

        endpoints.ShouldNotBeEmpty();

        var policyProvider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var endpoint in endpoints)
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "?";
            var path = endpoint.RoutePattern.RawText ?? string.Empty;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var label = $"{string.Join(",", methods)} {path} ({name})";

            ForbiddenPathSegments.ShouldNotContain(
                segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase),
                $"{label} must not expose bootstrap, setup, or recovery paths.");

            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

            if (AnonymousOperations.Contains(name))
            {
                anonymous.ShouldBeTrue($"{label} must be anonymous.");
                continue;
            }

            anonymous.ShouldBeFalse($"{label} must not allow anonymous access.");
            authorizeData.ShouldNotBeEmpty($"{label} must require authorization.");

            var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
            policy.ShouldNotBeNull();
            policy.AuthenticationSchemes.ShouldNotBeEmpty($"{label} must name an authentication scheme.");

            if (!policy.AuthenticationSchemes.Contains(SessionScheme))
            {
                continue;
            }

            var policyName = authorizeData.Select(data => data.Policy).LastOrDefault(value => value is not null);
            var expected = SessionHolderOperations.Contains(name) ? "SessionHolder" : "ActiveMember";
            policyName.ShouldBe(expected, $"{label} uses the wrong session policy.");

            if (methods.Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                endpoint.Metadata.Any(item => item.GetType().Name == "RequiresSessionAntiforgery")
                    .ShouldBeTrue($"{label} must require the session anti-forgery check.");
            }
        }
    }

    private static bool IsApiPath(RoutePattern pattern) =>
        pattern.RawText?.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) ?? false;

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:socalytics"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1",
                ["IdentityAccess:Session:IdleTimeout"] = "00:30:00",
                ["IdentityAccess:Session:AbsoluteLifetime"] = "08:00:00",
                ["IdentityAccess:Lockout:MaxFailedAccessAttempts"] = "5",
                ["IdentityAccess:Lockout:LockoutDuration"] = "00:15:00",
                ["IdentityAccess:Password:RequiredLength"] = "12",
                ["IdentityAccess:OneTimeCredential:Lifetime"] = "1.00:00:00",
            }));
            builder.ConfigureServices(services =>
            {
                // Keep the bootstrap service from touching the unreachable database or blocking host start.
                var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType?.Name == "ClubBootstrapHostedService").ToArray();
                foreach (var descriptor in hosted)
                {
                    services.Remove(descriptor);
                }
            });
        });
}
