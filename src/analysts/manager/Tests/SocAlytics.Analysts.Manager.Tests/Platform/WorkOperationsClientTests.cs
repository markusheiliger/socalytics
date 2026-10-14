#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Platform;

public class WorkOperationsClientTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MessageId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Granted =
        """{"attemptId":"22222222-2222-2222-2222-222222222222","logicalJobId":"11111111-1111-1111-1111-111111111111","readinessOccurrenceId":"33333333-3333-3333-3333-333333333333","attemptNumber":1,"fencingToken":7,"leaseExpiresAt":"2026-10-12T14:30:00Z","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}""";

    private const string Credentials =
        """{"userJwt":"jwt-secret","expiresAt":"2026-10-12T14:42:33Z","queueUrl":"tls://queue.test:4222","stream":"SOCALYTICS_ANALYSIS_JOBS","consumers":[{"capability":"player-detection","consumer":"analyst-player-detection"}],"inboxPrefix":"_INBOX_9f2c1e0a","maxConcurrentAnalysts":2}""";

    private readonly FakePlatform _platform;
    private readonly PlatformClient _client;
    private readonly InactiveSignalSink _sink = new();
    private readonly List<InactiveReason> _inactive = [];
    private readonly PlatformSession _session;

    public WorkOperationsClientTests()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
        _platform = new FakePlatform(time);
        _sink.Subscribe(r =>
        {
            _inactive.Add(r);
            return Task.CompletedTask;
        });
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(time), _sink);
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        _session = new PlatformSession(new Uri("https://platform.test"), key, "opaque-token");
    }

    private static string Problem(int status, string code) =>
        $$"""{"type":"urn:socalytics:problem:{{code}}","title":"t","status":{{status}},"code":"{{code}}","correlationId":"c"}""";

    private static AttemptCompletion Completion(bool succeeded) => new(
        "1.0.0", "team-1", "match-1", "run-1", "node-1", JobId.ToString(), AttemptId.ToString(), 1, 7,
        succeeded ? "succeeded" : "failed",
        succeeded ? new CompletionResult("results/run-1/job-1/manifest.json", "sha-256:" + new string('0', 64)) : null,
        succeeded ? null : new CompletionFailure("execution-timeout"),
        new CompletionCapability("player-detection", "1.0.0"), "1.0.0",
        new ExecutedRuntime("simulated", "1.0", "linux", "x86-64", []),
        "sha-256:" + new string('1', 64), null, [], DateTimeOffset.Parse("2026-10-12T14:10:00Z"),
        new CompletionCorrelation("corr-1"));

    [Fact]
    public async Task Queue_credentials_request_shape_and_dpop()
    {
        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.Created, Credentials);

        var result = await _client.RequestQueueCredentialsAsync(
            _session, "UDXU4RCSJNZOIQHZNWXHXORDPRTGNJAHAHFRGZNEEJCPQTT2M7NLCNF4", [new("player-detection", "1.0.0")]);

        result.Kind.ShouldBe(WorkResultKind.Success);
        result.Value!.InboxPrefix.ShouldBe("_INBOX_9f2c1e0a");
        result.Value.MaxConcurrentAnalysts.ShouldBe(2);
        result.Value.Consumers.Single().Consumer.ShouldBe("analyst-player-detection");
        var sent = _platform.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Uri.AbsolutePath.ShouldBe("/api/v1/analyst-managers/self/queue-credentials");
        var body = JsonNode.Parse(sent.Body)!;
        ((string)body["userPublicKey"]!).ShouldStartWith("U");
        ((string)body["capabilities"]![0]!["id"]!).ShouldBe("player-detection");
        body.AsObject().ContainsKey("userSeed").ShouldBeFalse();
    }

    [Fact]
    public async Task Claim_request_shape_and_replay()
    {
        _platform.Script(WorkOperation.Claim, HttpStatusCode.Created, Granted);
        var first = await _client.ClaimAsync(_session, JobId, OccurrenceId, MessageId, $"{MessageId}:{OccurrenceId}");
        first.Kind.ShouldBe(WorkResultKind.Success);
        first.Value!.FencingToken.ShouldBe(7);
        first.Value.LeaseSeconds.ShouldBe(60);

        var sent = _platform.Requests.Single();
        sent.Uri.AbsolutePath.ShouldBe($"/api/v1/analysis/logical-jobs/{JobId}/claims");
        var body = JsonNode.Parse(sent.Body)!;
        ((string)body["readinessOccurrenceId"]!).ShouldBe(OccurrenceId.ToString());
        ((string)body["messageId"]!).ShouldBe(MessageId.ToString());
        _platform.Headers.Single().IdempotencyKey.ShouldBe($"{MessageId}:{OccurrenceId}");

        _platform.Script(WorkOperation.Claim, HttpStatusCode.OK, Granted.Replace("\"replayed\":false", "\"replayed\":true"));
        (await _client.ClaimAsync(_session, JobId, OccurrenceId, MessageId, "k")).Kind.ShouldBe(WorkResultKind.Replayed);
    }

    [Fact]
    public async Task Heartbeat_request_shape()
    {
        _platform.Script(WorkOperation.Heartbeat, HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"2026-10-12T14:31:00Z"}""");
        var observed = DateTimeOffset.Parse("2026-10-12T14:00:30Z");

        var result = await _client.SendHeartbeatAsync(_session, AttemptId, JobId, 7, observed, "running");

        result.Kind.ShouldBe(WorkResultKind.Success);
        result.Value!.LeaseExpiresAt.ShouldBe(DateTimeOffset.Parse("2026-10-12T14:31:00Z"));
        var sent = _platform.Requests.Single();
        sent.Uri.AbsolutePath.ShouldBe($"/api/v1/analysis/attempts/{AttemptId}/heartbeats");
        var body = JsonNode.Parse(sent.Body)!;
        ((string)body["logicalJobId"]!).ShouldBe(JobId.ToString());
        ((long)body["fencingToken"]!).ShouldBe(7);
        ((string)body["executionState"]!).ShouldBe("running");
        DateTimeOffset.Parse((string)body["observedAt"]!).ShouldBe(observed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_follows_the_contract_and_carries_the_idempotency_key(bool succeeded)
    {
        _platform.Script(WorkOperation.Completion, HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":null,"replayed":false,"logicalJobState":"Completed"}""");

        var result = await _client.CompleteAsync(_session, AttemptId, Completion(succeeded), "idem-1");

        result.Kind.ShouldBe(WorkResultKind.Success);
        var sent = _platform.Requests.Single();
        sent.Uri.AbsolutePath.ShouldBe($"/api/v1/analysis/attempts/{AttemptId}/completion");
        var body = JsonNode.Parse(sent.Body)!.AsObject();
        ((string)body["idempotencyKey"]!).ShouldBe("idem-1");
        ((string)body["outcome"]!).ShouldBe(succeeded ? "succeeded" : "failed");
        body.ContainsKey("result").ShouldBe(succeeded);
        body.ContainsKey("failure").ShouldBe(!succeeded);
        body.ContainsKey("executedModelDigest").ShouldBeTrue();
        body["executedModelDigest"].ShouldBeNull();
        body["acceptedUpstreamResultIds"]!.AsArray().Count.ShouldBe(0);
        ((string)body["correlation"]!["correlationId"]!).ShouldBe("corr-1");
        body.Select(p => p.Key).ShouldAllBe(k => k == "result" || k == "failure" || char.IsLower(k[0]));
    }

    [Fact]
    public async Task Completion_replay_is_reported()
    {
        _platform.Script(WorkOperation.Completion, HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","acceptedResultId":null,"replayed":true,"logicalJobState":"Completed"}""");
        (await _client.CompleteAsync(_session, AttemptId, Completion(true), "idem-1")).Kind.ShouldBe(WorkResultKind.Replayed);
    }

    [Fact]
    public async Task Every_call_carries_a_dpop_proof_and_authorization()
    {
        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.Created, Credentials);
        _platform.Script(WorkOperation.Claim, HttpStatusCode.Created, Granted);
        _platform.Script(WorkOperation.Heartbeat, HttpStatusCode.OK, $$"""{"attemptId":"{{AttemptId}}","leaseExpiresAt":"2026-10-12T14:31:00Z"}""");
        _platform.Script(WorkOperation.Completion, HttpStatusCode.OK,
            $$"""{"attemptId":"{{AttemptId}}","logicalJobId":"{{JobId}}","outcome":"accepted","replayed":false,"logicalJobState":"Completed"}""");

        await _client.RequestQueueCredentialsAsync(_session, "UKEY", [new("player-detection", "1.0.0")]);
        await _client.ClaimAsync(_session, JobId, OccurrenceId, MessageId, "k");
        await _client.SendHeartbeatAsync(_session, AttemptId, JobId, 7, DateTimeOffset.UnixEpoch, "running");
        await _client.CompleteAsync(_session, AttemptId, Completion(true), "idem-1");

        _platform.Headers.Count.ShouldBe(4);
        foreach (var (_, dpop, _) in _platform.Headers)
        {
            dpop.ShouldNotBeNullOrEmpty();
        }

        _platform.Headers.Select(h => h.DPoP).Distinct().Count().ShouldBe(4);
        var proofs = _platform.Headers.Select(h => h.DPoP!.Split('.')[1]).ToList();
        foreach (var (payload, expected) in proofs.Zip(new[]
                 {
                     "/self/queue-credentials", "/claims", "/heartbeats", "/completion",
                 }))
        {
            var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Base64UrlDecode(payload)))!;
            ((string)json["htm"]!).ShouldBe("POST");
            ((string)json["htu"]!).ShouldEndWith(expected);
            json["ath"].ShouldNotBeNull();
        }
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    [InlineData("complete")]
    public async Task Obsolete_codes_map_to_obsolete(string operation)
    {
        var code = operation == "claim" ? "claim-obsolete" : "attempt-obsolete";
        var op = operation switch { "claim" => WorkOperation.Claim, "heartbeat" => WorkOperation.Heartbeat, _ => WorkOperation.Completion };
        _platform.Script(op, HttpStatusCode.Conflict, Problem(409, code));

        var kind = await Invoke(operation);

        kind.Kind.ShouldBe(WorkResultKind.Obsolete);
        kind.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    [InlineData("complete")]
    public async Task Other_conflicts_map_to_conflict(string operation)
    {
        var op = operation switch { "claim" => WorkOperation.Claim, "heartbeat" => WorkOperation.Heartbeat, _ => WorkOperation.Completion };
        _platform.Script(op, HttpStatusCode.Conflict, Problem(409, "idempotency-key-reused"));

        var result = await Invoke(operation);

        result.Kind.ShouldBe(WorkResultKind.Conflict);
        result.Code.ShouldBe("idempotency-key-reused");
    }

    [Fact]
    public async Task User_key_reuse_is_a_conflict_and_inactive_manager_is_refused()
    {
        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.Conflict, Problem(409, "user-key-reused"));
        var reused = await _client.RequestQueueCredentialsAsync(_session, "UKEY", []);
        reused.Kind.ShouldBe(WorkResultKind.Conflict);

        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.Forbidden, Problem(403, "manager-not-active"));
        var refused = await _client.RequestQueueCredentialsAsync(_session, "UKEY", []);
        refused.Kind.ShouldBe(WorkResultKind.Refused);
        refused.Code.ShouldBe("manager-not-active");
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    [InlineData("complete")]
    public async Task Inactive_responses_are_routed_to_the_sink(string operation)
    {
        var op = Operation(operation);
        _platform.Script(op, () =>
        {
            var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"type":"urn:socalytics:problem:analyst-manager-registration-inactive","title":"t","status":401,"code":"analyst-manager-registration-inactive","correlationId":"c","registrationState":"revoked"}"""),
            };
            denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "DPoP error=\"invalid_token\"");
            return denied;
        });

        var result = await Invoke(operation);

        result.Kind.ShouldBe(WorkResultKind.Inactive);
        _inactive.ShouldBe([InactiveReason.Revoked]);
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    [InlineData("complete")]
    public async Task Server_errors_and_network_failures_are_transient(string operation)
    {
        var op = Operation(operation);

        _platform.Script(op, HttpStatusCode.ServiceUnavailable, "");
        (await Invoke(operation)).Kind.ShouldBe(WorkResultKind.Transient);

        _platform.Script(op, () => throw new HttpRequestException("unreachable"));
        (await Invoke(operation)).Kind.ShouldBe(WorkResultKind.Transient);

        _platform.Script(op, () => throw new TaskCanceledException("timeout"));
        (await Invoke(operation)).Kind.ShouldBe(WorkResultKind.Transient);
        _inactive.ShouldBeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_transient()
    {
        _platform.Script(WorkOperation.Claim, () => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _client.ClaimAsync(_session, JobId, OccurrenceId, MessageId, "k", cts.Token));
    }

    [Fact]
    public async Task Plaintext_platform_endpoint_is_refused_before_sending()
    {
        var insecure = _session with { BaseUri = new Uri("http://platform.test") };

        await Should.ThrowAsync<PlatformException>(
            () => _client.ClaimAsync(insecure, JobId, OccurrenceId, MessageId, "k"));
        _platform.Requests.ShouldBeEmpty();
    }

    private static WorkOperation Operation(string operation) => operation switch
    {
        "credentials" => WorkOperation.QueueCredentials,
        "claim" => WorkOperation.Claim,
        "heartbeat" => WorkOperation.Heartbeat,
        _ => WorkOperation.Completion,
    };

    private async Task<WorkResult<object>> Invoke(string operation)
    {
        switch (operation)
        {
            case "credentials":
                var c = await _client.RequestQueueCredentialsAsync(_session, "UKEY", []);
                return new(c.Kind, c.Value, c.Code);
            case "claim":
                var g = await _client.ClaimAsync(_session, JobId, OccurrenceId, MessageId, "k");
                return new(g.Kind, g.Value, g.Code);
            case "heartbeat":
                var h = await _client.SendHeartbeatAsync(_session, AttemptId, JobId, 7, DateTimeOffset.UnixEpoch, "running");
                return new(h.Kind, h.Value, h.Code);
            default:
                var d = await _client.CompleteAsync(_session, AttemptId, Completion(true), "idem-1");
                return new(d.Kind, d.Value, d.Code);
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
