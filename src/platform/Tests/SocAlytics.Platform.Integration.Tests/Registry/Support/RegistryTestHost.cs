using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

internal static class RegistryTestHost
{
    public static PlatformApiFactory Create(IsolatedDatabase db, SoftwareKeyPolicy softwareKeys = SoftwareKeyPolicy.All, Action<RegistryOptions.RateLimitOptions>? rateLimits = null) =>
        new(db, new Dictionary<string, string?>
        {
            ["ClubDisplayName"] = "Registry Club",
            ["FirstClubAdmin:AccountName"] = "registry-admin",
            ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
        }, configureServices: services => services.Configure<RegistryOptions>(o =>
        {
            o.SoftwareKeys = softwareKeys;
            o.RateLimits.PairingCreationPerMinute = 10_000;
            o.RateLimits.StatusPerMinute = 10_000;
            o.RateLimits.ActivationPerMinute = 10_000;
            o.RateLimits.CodeSubmissionPerMinute = 10_000;
            rateLimits?.Invoke(o.RateLimits);
        }));

    public static object PairingBody(TestAnalystManager manager, string protection = "tpm", string stampId = "local-dev-stamp", string? profile = null) => new
    {
        stampId,
        publicKey = manager.PublicJwkObject(),
        nonce = "nonce-0123456789abcdef",
        deviceMetadata = new { os = "linux", osVersion = "6.1", architecture = "x64", managerVersion = "1.0.0", claimedKeyProtection = protection, claimedApplicationProfile = profile },
    };

    public static Task<HttpResponseMessage> PairAsync(HttpClient client, object body, CancellationToken ct) =>
        client.PostAsJsonAsync("/api/v1/analyst-managers/pairings", body, ct);

    public static Task<HttpResponseMessage> StatusAsync(HttpClient client, string handle, CancellationToken ct) =>
        client.PostAsJsonAsync("/api/v1/analyst-managers/pairings/status", new { pollingHandle = handle }, ct);

    public static string WithoutCorrelation(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("correlationId");
        return node.ToJsonString();
    }
}
