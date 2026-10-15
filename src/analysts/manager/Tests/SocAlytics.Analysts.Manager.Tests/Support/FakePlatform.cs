using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

internal enum WorkOperation
{
    QueueCredentials,
    Claim,
    Heartbeat,
    Completion,
    ResultGrants,
}

/// <summary>In-process platform: every openapi.yaml operation with configurable outcomes.</summary>
internal sealed class FakePlatform(TimeProvider time) : HttpMessageHandler
{
    public const string PairingCode = "BCDF-GHJK-LMNP";
    public const string PollingHandle = "polling-handle-0123456789-0123456789-0123456789-abcdef";

    private PublicJwk? _publicKey;
    private string? _challenge;
    private int _challengeCounter;

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>DPoP proof and idempotency header of every work operation call.</summary>
    public List<(WorkOperation Operation, string? DPoP, string? IdempotencyKey)> Headers { get; } = [];

    private Dictionary<WorkOperation, Func<HttpResponseMessage>> WorkScripts { get; } = [];

    /// <summary>Scripts the response of a work operation; a script that throws simulates an unreachable platform.</summary>
    public void Script(WorkOperation operation, Func<HttpResponseMessage> response) => WorkScripts[operation] = response;

    public void Script(WorkOperation operation, HttpStatusCode status, string body = "")
    {
        WorkScripts[operation] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    public string StampId { get; set; } = "stamp-1";

    public bool SoftwareKeysAllowed { get; set; }

    public bool CorruptFingerprint { get; set; }

    public int PendingPolls { get; set; } = 1;

    public string TerminalStatus { get; set; } = "approved";

    public string OwnState { get; set; } = "active";

    public int UnreachableCount { get; set; }

    public bool Inactive { get; set; }

    public int UnregistrationUnreachableCount { get; set; }

    public bool InactiveOnUnregistration { get; set; }

    public HttpStatusCode? SelfStatus { get; set; }

    public string? OwnStampId { get; set; }

    public double IntervalSeconds { get; set; } = 60;

    public List<ReportViolation>? ReportViolations { get; set; }

    public IEnumerable<JsonNode> StatusReports => Requests
        .Where(r => r.Uri.AbsolutePath.EndsWith("/self/status", StringComparison.Ordinal))
        .Select(r => JsonNode.Parse(r.Body)!);

    public int TokenRequests => Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal));

    public int SelfCalls => Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/self/registration", StringComparison.Ordinal));

    public void Adopt(PublicJwk key)
    {
        _publicKey = key;
        ManagerId = "manager-1";
    }

    public int PollCount { get; private set; }

    public bool Activated { get; private set; }

    public string? ClaimedKeyProtection { get; private set; }

    public string? ManagerId { get; private set; }

    public string Fingerprint { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        }

        var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/analyst-managers/", string.Empty);
        var own = path is "self/registration" or "self/status";
        if (path is "token" || own)
        {
            if (UnreachableCount > 0)
            {
                UnreachableCount--;
                throw new HttpRequestException("unreachable");
            }

            if (own && SelfStatus is { } forced)
            {
                return new HttpResponseMessage(forced);
            }

            if (Inactive && own)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "{\"type\":\"https://platform.test/problems/analyst-manager-registration-inactive\",\"title\":\"Inactive\",\"status\":401,\"code\":\"analyst-manager-registration-inactive\",\"correlationId\":\"c\",\"registrationState\":\"revoked\"}"),
                };
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "DPoP error=\"invalid_token\"");
                return denied;
            }

            if (Inactive && path == "token")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid_client\",\"registration_state\":\"revoked\"}"),
                };
            }
        }

        if (path.StartsWith("self/unregistration", StringComparison.Ordinal))
        {
            if (UnregistrationUnreachableCount > 0)
            {
                UnregistrationUnreachableCount--;
                throw new HttpRequestException("unreachable");
            }

            if (InactiveOnUnregistration)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "{\"type\":\"https://platform.test/problems/analyst-manager-registration-inactive\",\"title\":\"Inactive\",\"status\":401,\"code\":\"analyst-manager-registration-inactive\",\"correlationId\":\"c\",\"registrationState\":\"revoked\"}"),
                };
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "DPoP error=\"invalid_token\"");
                return denied;
            }
        }

        var workPath = request.RequestUri!.AbsolutePath;
        if (workPath.EndsWith("/self/queue-credentials", StringComparison.Ordinal)
            || workPath.StartsWith("/api/v1/analysis/", StringComparison.Ordinal))
        {
            var operation = workPath.EndsWith("/self/queue-credentials", StringComparison.Ordinal) ? WorkOperation.QueueCredentials
                : workPath.EndsWith("/claims", StringComparison.Ordinal) ? WorkOperation.Claim
                : workPath.EndsWith("/heartbeats", StringComparison.Ordinal) ? WorkOperation.Heartbeat
                : workPath.EndsWith("/result-grants", StringComparison.Ordinal) ? WorkOperation.ResultGrants
                : WorkOperation.Completion;
            lock (Requests)
            {
                Headers.Add((operation, request.Headers.TryGetValues("DPoP", out var dpop) ? dpop.FirstOrDefault() : null,
                    request.Headers.TryGetValues("Idempotency-Key", out var idem) ? idem.FirstOrDefault() : null));
            }

            if (WorkScripts.TryGetValue(operation, out var script))
            {
                return script();
            }

            if (operation == WorkOperation.ResultGrants)
            {
                var objects = JsonNode.Parse(body)!["objects"]!.AsArray();
                var grants = objects.Select(o => new JsonObject
                {
                    ["name"] = (string?)o!["name"],
                    ["method"] = "PUT",
                    ["url"] = $"https://storage.test/results/{(string?)o["name"]}?X-Amz-Signature=0000",
                    ["requiredHeaders"] = new JsonObject { ["Content-Length"] = ((long)o["sizeBytes"]!).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ["expiresAt"] = time.GetUtcNow().AddMinutes(5).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                }).ToArray<JsonNode?>();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        new JsonObject { ["resultLocation"] = "results/r/j/a/", ["grants"] = new JsonArray(grants) }.ToJsonString(),
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }
        }

        return path switch
        {
            "stamp" => Json(HttpStatusCode.OK, new StampDiscovery(
                StampId, SoftwareKeysAllowed, Url("pairings"), Url("pairings/activation"), Url("token"))),
            "pairings" => Pairing(body),
            "pairings/status" => Status(),
            "pairings/activation" => Activation(body),
            "token" => Json(HttpStatusCode.OK, new TokenResponse("opaque-token", "DPoP", 300, "analyst-manager")),
            "self/registration" => Own(OwnState),
            "self/status" => Own(OwnState, ReportViolations is { Count: > 0 } ? ReportViolations : null),
            "self/unregistration" => Own("unregistering"),
            "self/unregistration/completion" => Json(HttpStatusCode.OK, new UnregistrationResult(ManagerId ?? "m", "unregistered")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static string Url(string path) => $"https://platform.test/api/v1/analyst-managers/{path}";

    private HttpResponseMessage Pairing(string body)
    {
        var node = JsonNode.Parse(body)!;
        _publicKey = PublicJwk.FromJson(node["publicKey"]!.ToJsonString());
        ClaimedKeyProtection = (string?)node["deviceMetadata"]!["claimedKeyProtection"];
        Fingerprint = DeviceFingerprint.Compute(_publicKey);
        return Json(HttpStatusCode.Created, new PairingIssued(
            PairingCode,
            CorruptFingerprint ? "BBBB-BBBB" : Fingerprint,
            PollingHandle,
            "https://platform.test/registrar",
            time.GetUtcNow().AddMinutes(10),
            1,
            StampId));
    }

    private HttpResponseMessage Status()
    {
        PollCount++;
        if (PollCount <= PendingPolls)
        {
            return Json(HttpStatusCode.OK, new PairingStatus("pending_approval", 1));
        }

        if (TerminalStatus != "approved")
        {
            return Json(HttpStatusCode.OK, new PairingStatus(TerminalStatus, 1));
        }

        _challenge = $"challenge-{++_challengeCounter}";
        return Json(HttpStatusCode.OK, new PairingStatus("approved", 1, null, _challenge, time.GetUtcNow().AddMinutes(1)));
    }

    private HttpResponseMessage Activation(string body)
    {
        var proof = (string)JsonNode.Parse(body)!["proof"]!;
        var challenge = _challenge;
        _challenge = null;
        if (_publicKey is null || challenge is null
            || !CompactJws.TryVerify(proof, _publicKey, out var header, out var payload)
            || (string?)header["typ"] != "analyst-manager-activation+jwt"
            || (string?)payload["challenge"] != challenge
            || (string?)payload["aud"] != Url("pairings/activation"))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        Activated = true;
        ManagerId = "manager-1";
        return Json(HttpStatusCode.OK, new ActivationResult(ManagerId, StampId, Url("token"), JwkThumbprint.Compute(_publicKey)));
    }

    private HttpResponseMessage Own(string state, IReadOnlyList<ReportViolation>? violations = null) => Json(HttpStatusCode.OK, new OwnRegistration(
        ManagerId ?? "manager-1", OwnStampId ?? StampId, state, "Club", _publicKey is null ? string.Empty : JwkThumbprint.Compute(_publicKey),
        time.GetUtcNow(), IntervalSeconds, violations is null, violations));

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: PlatformJson.Options) };
}
