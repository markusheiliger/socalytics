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

public sealed class PlatformClient(HttpClient http, ProofFactory proofs)
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
        return new PlatformResponse(response.StatusCode, body, response.Headers.WwwAuthenticate.Select(h => h.ToString()).ToList());
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
