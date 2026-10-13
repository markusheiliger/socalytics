using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.Platform;

public sealed class PlatformException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Raw response; bodies may carry secrets and are never logged.</summary>
public sealed record PlatformResponse(System.Net.HttpStatusCode Status, string Body, IReadOnlyList<string> WwwAuthenticate)
{
    public T? Read<T>() where T : class =>
        string.IsNullOrEmpty(Body) ? null : JsonSerializer.Deserialize<T>(Body, PlatformJson.Options);
}

/// <summary>Single hub through which every platform response reports an inactive (revoked or unregistered) signal.</summary>
public sealed class InactiveSignalSink
{
    private Func<InactiveReason, Task>? _handler;

    public void Subscribe(Func<InactiveReason, Task> handler) => _handler = handler;

    public Task ReportAsync(InactiveReason reason) => _handler?.Invoke(reason) ?? Task.CompletedTask;
}

public sealed class PlatformClient(HttpClient http, ProofFactory proofs, InactiveSignalSink? sink = null)
{
    private const string Prefix = "api/v1/analyst-managers/";

    public static Uri Endpoint(Uri baseUri, string path)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PlatformException(ManagerDiagnosticCodes.InsecureEndpoint);
        }

        var root = baseUri.GetLeftPart(UriPartial.Authority);
        return new Uri($"{root}/{Prefix}{path}");
    }

    public HttpRequestMessage BuildDiscovery(Uri baseUri) => new(HttpMethod.Get, Endpoint(baseUri, "stamp"));

    public HttpRequestMessage BuildPairing(Uri baseUri, string stampId, IDeviceKey key, DeviceMetadata metadata) =>
        Json(Endpoint(baseUri, "pairings"), new PairingRequest(
            stampId,
            JsonSerializer.SerializeToElement(key.PublicJwk.ToJsonObject()),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            metadata));

    public HttpRequestMessage BuildPairingStatus(Uri baseUri, string pollingHandle) =>
        Json(Endpoint(baseUri, "pairings/status"), new PollingHandleRequest(pollingHandle));

    public HttpRequestMessage BuildActivation(Uri baseUri, IDeviceKey key, string pollingHandle, string challenge)
    {
        var uri = Endpoint(baseUri, "pairings/activation");
        return Json(uri, new ActivationRequest(pollingHandle, proofs.ActivationProof(key, uri.AbsoluteUri, challenge)));
    }

    public HttpRequestMessage BuildToken(Uri baseUri, IDeviceKey key, string managerId)
    {
        var uri = Endpoint(baseUri, "token");
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = managerId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = proofs.ClientAssertion(key, managerId, uri.AbsoluteUri),
                ["scope"] = "analyst-manager",
            }),
        };
        request.Headers.Add("DPoP", proofs.DPoP(key, "POST", uri.AbsoluteUri));
        return request;
    }

    public HttpRequestMessage BuildSelfRegistration(Uri baseUri, IDeviceKey key, string accessToken) =>
        Self(HttpMethod.Get, Endpoint(baseUri, "self/registration"), key, accessToken);

    public HttpRequestMessage BuildStatusReport(Uri baseUri, IDeviceKey key, string accessToken, StatusReport report)
    {
        var request = Self(HttpMethod.Post, Endpoint(baseUri, "self/status"), key, accessToken);
        request.Content = JsonContent.Create(report, options: PlatformJson.Options);
        return request;
    }

    public HttpRequestMessage BuildUnregistration(Uri baseUri, IDeviceKey key, string accessToken) =>
        Self(HttpMethod.Post, Endpoint(baseUri, "self/unregistration"), key, accessToken);

    public HttpRequestMessage BuildUnregistrationCompletion(Uri baseUri, IDeviceKey key, string accessToken) =>
        Self(HttpMethod.Post, Endpoint(baseUri, "self/unregistration/completion"), key, accessToken);

    public Task<PlatformResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new PlatformException(ManagerDiagnosticCodes.InsecureEndpoint);
        }

        return SendCoreAsync(request, cancellationToken);
    }

    private async Task<PlatformResponse> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = new PlatformResponse(response.StatusCode, body, response.Headers.WwwAuthenticate.Select(h => h.ToString()).ToList());
        var path = request.RequestUri!.AbsolutePath;
        var signal = path.EndsWith("/token", StringComparison.Ordinal)
            ? PlatformSignals.ClassifyToken(result.Status, body)
            : path.Contains("/self/", StringComparison.Ordinal)
                ? PlatformSignals.ClassifyResource(result.Status, result.WwwAuthenticate, body)
                : null;
        if (sink is not null && signal is { Outcome: PlatformOutcome.Inactive, Reason: { } reason })
        {
            await sink.ReportAsync(reason);
        }

        return result;
    }

    private HttpRequestMessage Self(HttpMethod method, Uri uri, IDeviceKey key, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"DPoP {accessToken}");
        request.Headers.Add("DPoP", proofs.DPoP(key, method.Method, uri.AbsoluteUri, accessToken));
        return request;
    }

    private static HttpRequestMessage Json<T>(Uri uri, T body) =>
        new(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: PlatformJson.Options) };
}
