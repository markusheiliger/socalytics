using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

/// <summary>Registers an Analyst Manager through the real pairing flow and sends DPoP-bound requests; all timestamps come from the host clock.</summary>
internal sealed class RegisteredAnalystManagerClient : IDisposable
{
    private const string Origin = "https://localhost";
    private const string TokenPath = "/api/v1/analyst-managers/token";

    private readonly AnalysisTestHost _host;
    private readonly TestAnalystManager _key;
    private readonly HttpClient _client;
    private readonly ApiSession _admin;
    private readonly Guid _registrationId;
    private string _token = "";

    private RegisteredAnalystManagerClient(AnalysisTestHost host, HttpClient client, ApiSession admin, TestAnalystManager key, Guid registrationId, Guid managerId)
    {
        _key = key;
        _host = host;
        _client = client;
        _admin = admin;
        _registrationId = registrationId;
        ManagerId = managerId;
    }

    public Guid ManagerId { get; }

    public static async Task<RegisteredAnalystManagerClient> RegisterAsync(
        AnalysisTestHost host, IsolatedDatabase db, CancellationToken ct)
    {
        await TestMembers.SeedAsync(db, "registrar-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(host.Factory, "registrar-1", TestMembers.DefaultPassword, ct);
        var client = host.Factory.CreateApiClient();
        var key = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(key), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created, await pair.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, "/api/v1/analyst-manager-registrations",
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var st = await RegistryTestHost.StatusAsync(client, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = key.ActivationProof(host.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var act = await client.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;

        var result = new RegisteredAnalystManagerClient(host, client, admin, key, id, Guid.Parse(clientId));
        await result.IssueTokenAsync(clientId, ct);
        using var self = await result.SendAsync(HttpMethod.Get, "/api/v1/analyst-managers/self/registration", null, null, ct);
        self.StatusCode.ShouldBe(HttpStatusCode.OK);
        return result;
    }

    private async Task IssueTokenAsync(string clientId, CancellationToken ct)
    {
        var now = _host.Time.GetUtcNow();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = _key.ClientAssertion(now, Origin + TokenPath, clientId),
                ["client_id"] = clientId,
            }),
        };
        request.Headers.Add("DPoP", _key.DPoPProof(now, "POST", Origin + TokenPath));
        using var response = await _client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, IDictionary<string, string>? headers, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + _token);
        request.Headers.Add("DPoP", _key.DPoPProof(_host.Time.GetUtcNow(), method.Method, Origin + path.Split('?')[0], _token));
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(k, v);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request, ct);
    }

    public Task<HttpResponseMessage> ClaimAsync(Guid jobId, Guid occurrenceId, string key, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/v1/analysis/logical-jobs/{jobId}/claims",
            new { readinessOccurrenceId = occurrenceId, messageId = Guid.NewGuid() },
            new Dictionary<string, string> { ["Idempotency-Key"] = key }, ct);

    public async Task RevokeAsync(CancellationToken ct)
    {
        using var response = await _admin.SendAsync(
            HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{_registrationId}/revocation", new { reason = "test" }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        _client.Dispose();
        _admin.Dispose();
        _key.Dispose();
            }
}
