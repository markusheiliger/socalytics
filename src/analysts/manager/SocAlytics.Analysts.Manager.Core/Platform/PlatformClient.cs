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

    public HttpRequestMessage BuildUnregistration(Uri baseUri, IDeviceKey key, string accessToken) =>
        Self(HttpMethod.Post, Endpoint(baseUri, "self/unregistration"), key, accessToken);

    public HttpRequestMessage BuildUnregistrationCompletion(Uri baseUri, IDeviceKey key, string accessToken) =>
        Self(HttpMethod.Post, Endpoint(baseUri, "self/unregistration/completion"), key, accessToken);

    public static Uri AnalysisEndpoint(Uri baseUri, string path)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PlatformException(ManagerDiagnosticCodes.InsecureEndpoint);
        }

        return new Uri($"{baseUri.GetLeftPart(UriPartial.Authority)}/api/v1/analysis/{path}");
    }

    public async Task<WorkResult<QueueCredentialsResponse>> RequestQueueCredentialsAsync(
        PlatformSession session, string userPublicKey, IReadOnlyList<QueueCapability> capabilities,
        CancellationToken cancellationToken = default)
    {
        var uri = Endpoint(session.BaseUri, "self/queue-credentials");
        var request = Authenticated(HttpMethod.Post, uri, session);
        request.Content = JsonContent.Create(new QueueCredentialsRequest(userPublicKey, capabilities), options: PlatformJson.Options);
        return await ExecuteAsync<QueueCredentialsResponse>(request, cancellationToken);
    }

    public async Task<WorkResult<ClaimGranted>> ClaimAsync(
        PlatformSession session, Guid logicalJobId, Guid readinessOccurrenceId, Guid messageId, string retryKey,
        CancellationToken cancellationToken = default)
    {
        var uri = AnalysisEndpoint(session.BaseUri, $"logical-jobs/{logicalJobId}/claims");
        var request = Authenticated(HttpMethod.Post, uri, session);
        request.Headers.Add("Idempotency-Key", retryKey);
        request.Content = JsonContent.Create(new ClaimRequest(readinessOccurrenceId, messageId), options: PlatformJson.Options);
        return await ExecuteAsync<ClaimGranted>(request, cancellationToken, g => g.Replayed);
    }

    public async Task<WorkResult<LeaseRenewed>> SendHeartbeatAsync(
        PlatformSession session, Guid attemptId, Guid logicalJobId, long fencingToken, DateTimeOffset observedAt,
        string executionState, CancellationToken cancellationToken = default)
    {
        var uri = AnalysisEndpoint(session.BaseUri, $"attempts/{attemptId}/heartbeats");
        var request = Authenticated(HttpMethod.Post, uri, session);
        request.Content = JsonContent.Create(
            new HeartbeatRequest(logicalJobId, fencingToken, observedAt, executionState), options: PlatformJson.Options);
        return await ExecuteAsync<LeaseRenewed>(request, cancellationToken);
    }

    public async Task<WorkResult<CompletionOutcome>> CompleteAsync(
        PlatformSession session, Guid attemptId, AttemptCompletion completion, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var uri = AnalysisEndpoint(session.BaseUri, $"attempts/{attemptId}/completion");
        var body = JsonSerializer.SerializeToNode(completion, PlatformJson.Options)!.AsObject();
        body["idempotencyKey"] = idempotencyKey;
        // The contract's timestamp pattern requires the UTC designator "Z", not "+00:00".
        body["completedAt"] = completion.CompletedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (!body.ContainsKey("executedModelDigest"))
        {
            body["executedModelDigest"] = null;
        }

        var request = Authenticated(HttpMethod.Post, uri, session);
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return await ExecuteAsync<CompletionOutcome>(request, cancellationToken, c => c.Replayed);
    }

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
                : path.StartsWith("/api/v1/analysis/", StringComparison.Ordinal)
                    ? PlatformSignals.ClassifyResource(result.Status, result.WwwAuthenticate, body)
                    : null;
        if (sink is not null && signal is { Outcome: PlatformOutcome.Inactive, Reason: { } reason })
        {
            await sink.ReportAsync(reason);
        }

        return result;
    }

    private async Task<WorkResult<T>> ExecuteAsync<T>(
        HttpRequestMessage request, CancellationToken cancellationToken, Func<T, bool>? isReplay = null) where T : class
    {
        PlatformResponse response;
        try
        {
            response = await SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new(WorkResultKind.Transient);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(WorkResultKind.Transient);
        }

        var signal = PlatformSignals.ClassifyResource(response.Status, response.WwwAuthenticate, response.Body);
        switch (signal.Outcome)
        {
            case PlatformOutcome.Success:
                var value = response.Read<T>();
                if (value is null)
                {
                    return new(WorkResultKind.Refused);
                }

                return new(isReplay?.Invoke(value) == true ? WorkResultKind.Replayed : WorkResultKind.Success, value);
            case PlatformOutcome.Inactive:
                return new(WorkResultKind.Inactive);
            case PlatformOutcome.Transient:
                return new(WorkResultKind.Transient);
        }

        var code = ReadCode(response.Body);
        return code switch
        {
            "claim-obsolete" or "attempt-obsolete" => new(WorkResultKind.Obsolete, Code: code),
            _ when response.Status == System.Net.HttpStatusCode.Conflict => new(WorkResultKind.Conflict, Code: code),
            _ => new(WorkResultKind.Refused, Code: code),
        };
    }

    private static string? ReadCode(string body)
    {
        try
        {
            return string.IsNullOrEmpty(body) ? null : JsonSerializer.Deserialize<Problem>(body, PlatformJson.Options)?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpRequestMessage Authenticated(HttpMethod method, Uri uri, PlatformSession session) =>
        Self(method, uri, session.Key, session.AccessToken);

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
