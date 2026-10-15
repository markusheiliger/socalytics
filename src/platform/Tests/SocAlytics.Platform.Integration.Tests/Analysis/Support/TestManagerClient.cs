using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

internal sealed class TestManagerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string ManagerHeader = "X-Test-Manager";
    public const string NoScopeHeader = "X-Test-No-Scope";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ManagerHeader, out var id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(AnalystManagerClaimTypes.ManagerId, id.ToString()),
            new(AnalystManagerClaimTypes.RegistrationId, Guid.NewGuid().ToString()),
        };
        if (!Request.Headers.ContainsKey(NoScopeHeader))
        {
            claims.Add(new(AnalystManagerClaimTypes.Scope, AnalystManagerDPoPDefaults.ScopeValue));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class TestManagerClient : IDisposable
{
    private readonly HttpClient _client;

    public TestManagerClient(PlatformApiFactory factory, Guid? managerId = null)
    {
        _client = factory.CreateApiClient();
        ManagerId = managerId ?? Guid.NewGuid();
    }

    public Guid ManagerId { get; }

    public Task<HttpResponseMessage> ClaimAsync(
        Guid jobId, Guid occurrenceId, string? key, bool noScope = false, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/logical-jobs/{jobId}/claims")
        {
            Content = JsonContent.Create(new { readinessOccurrenceId = occurrenceId, messageId = Guid.NewGuid() }),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, ManagerId.ToString());
        if (noScope)
        {
            request.Headers.Add(TestManagerAuthenticationHandler.NoScopeHeader, "1");
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return _client.SendAsync(request, ct);
    }

    public Task<HttpResponseMessage> HeartbeatAsync(Guid attemptId, Guid jobId, long fencingToken, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attemptId}/heartbeats")
        {
            Content = JsonContent.Create(new
            {
                logicalJobId = jobId,
                fencingToken,
                observedAt = DateTimeOffset.UtcNow,
                executionState = "running",
            }),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, ManagerId.ToString());
        return _client.SendAsync(request, ct);
    }

    public Task<HttpResponseMessage> RequestResultGrantsAsync(Guid attemptId, object body, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attemptId}/result-grants")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, ManagerId.ToString());
        return _client.SendAsync(request, ct);
    }

    public void Dispose() => _client.Dispose();
}
