using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

/// <summary>In-process platform: every openapi.yaml operation with configurable outcomes.</summary>
internal sealed class FakePlatform(TimeProvider time) : HttpMessageHandler
{
    public const string PairingCode = "BCDF-GHJK-LMNP";
    public const string PollingHandle = "polling-handle-0123456789-0123456789-0123456789-abcdef";

    private PublicJwk? _publicKey;
    private string? _challenge;
    private int _challengeCounter;

    public List<RecordedRequest> Requests { get; } = [];

    public string StampId { get; set; } = "stamp-1";

    public bool SoftwareKeysAllowed { get; set; }

    public bool CorruptFingerprint { get; set; }

    public int PendingPolls { get; set; } = 1;

    public string TerminalStatus { get; set; } = "approved";

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
        return path switch
        {
            "stamp" => Json(HttpStatusCode.OK, new StampDiscovery(
                StampId, SoftwareKeysAllowed, Url("pairings"), Url("pairings/activation"), Url("token"))),
            "pairings" => Pairing(body),
            "pairings/status" => Status(),
            "pairings/activation" => Activation(body),
            "token" => Json(HttpStatusCode.OK, new TokenResponse("opaque-token", "DPoP", 300, "analyst-manager")),
            "self/registration" => Own("active"),
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

    private HttpResponseMessage Own(string state) => Json(HttpStatusCode.OK, new OwnRegistration(
        ManagerId ?? "manager-1", StampId, state, "Club", _publicKey is null ? string.Empty : JwkThumbprint.Compute(_publicKey),
        time.GetUtcNow(), 60));

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: PlatformJson.Options) };
}
