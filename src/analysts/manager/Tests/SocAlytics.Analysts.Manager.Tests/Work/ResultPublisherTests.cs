#pragma warning disable xUnit1051 // The fakes answer synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public sealed class ResultPublisherTests : IDisposable
{
    private static readonly Guid AttemptId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-15T10:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly FakeStorage _storage = new();
    private readonly ResultPublisher _publisher;
    private readonly PlatformSession _session;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "result-publisher-test-" + Guid.NewGuid().ToString("N"));

    public ResultPublisherTests()
    {
        _platform = new FakePlatform(_time);
        var client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        _publisher = new ResultPublisher(client, new HttpClient(_storage), _time, false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static AnalystJobMessage Job() => new(
        Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "run-1", "node-1", Guid.NewGuid().ToString(), "team-1", "match-1",
        new QueueCapability("match-summary", "1.0.0"), "1.0.0", "match",
        [new JobInput([new JobAcceptedResult("res-1", new QueueCapability("player-detection", "1.0.0"), "1.0.0", "sha-256:" + new string('1', 64))])],
        new JobPinned("sha-256:" + new string('3', 64), "sha-256:" + new string('4', 64), null, "profile-1", []),
        new JobResources(500, 1024), new CompletionCorrelation("corr-1"), "rsv-1");

    private ActiveAttempt Attempt() => new(
        Job(), new ClaimGranted(AttemptId, Guid.NewGuid(), Guid.NewGuid(), 1, 7, _time.GetUtcNow().AddSeconds(60), 60, 120, 600, false),
        _time.GetUtcNow());

    private void WriteValidResult(ActiveAttempt attempt) => SimulatedResultWriter.Write(
        new AttemptLaunch(
            attempt.AttemptId.ToString(), attempt.LogicalJobId.ToString(), attempt.Capability, "d", "match", "p", "i", "m",
            new AttemptResources(1, 1, []), attempt.Job, attempt.AttemptNumber, _directory),
        new SimulatedAnalystOptions());

    private Task<ResultPublishResult> Publish(ActiveAttempt attempt) =>
        _publisher.PublishAsync(attempt, _directory, () => _session, CancellationToken.None);

    [Fact]
    public async Task Valid_result_is_uploaded_with_only_the_required_headers_and_reported()
    {
        var attempt = Attempt();
        WriteValidResult(attempt);

        var result = await Publish(attempt);

        result.End.ShouldBe(ResultPublishEnd.Published);
        result.ManifestReference.ShouldBe("results/r/j/a/manifest.json");
        result.ManifestChecksum.ShouldBe(ResultPublisher.Digest(File.ReadAllBytes(Path.Combine(_directory, "manifest.json"))));
        _storage.Puts.Select(p => p.Uri.AbsolutePath).Order().ShouldBe(["/results/facts.json", "/results/manifest.json"]);
        _storage.Puts.ShouldAllBe(p => !p.HadAuthorization);
        _storage.Puts.ShouldAllBe(p => p.Headers.Keys.All(k => k == "Content-Length" || k == "Content-Type" || k == "Host"));
        var request = JsonNode.Parse(_platform.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/result-grants", StringComparison.Ordinal)).Body)!;
        ((long)request["fencingToken"]!).ShouldBe(7);
        request["objects"]!.AsArray().Select(o => (string)o!["name"]!).Order().ShouldBe(["facts.json", "manifest.json"]);
    }

    [Fact]
    public async Task Schema_invalid_manifest_is_not_uploaded()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), """{"contractVersion":"1.0.0"}""");

        var result = await Publish(Attempt());

        result.End.ShouldBe(ResultPublishEnd.Invalid);
        _storage.Puts.ShouldBeEmpty();
        _platform.Requests.ShouldNotContain(r => r.Uri.AbsolutePath.EndsWith("/result-grants", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forbidden_upload_renews_the_grants()
    {
        var attempt = Attempt();
        WriteValidResult(attempt);
        _storage.Scripted.Enqueue(HttpStatusCode.Forbidden);

        var result = await Publish(attempt);

        result.End.ShouldBe(ResultPublishEnd.Published);
        _platform.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/result-grants", StringComparison.Ordinal)).ShouldBe(2);
        _storage.Puts.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "attempt-obsolete")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    public async Task Refused_grants_stop_the_attempt(HttpStatusCode status, string code)
    {
        var attempt = Attempt();
        WriteValidResult(attempt);
        _platform.Script(WorkOperation.ResultGrants, status,
            $$"""{"type":"urn:socalytics:problem:{{code}}","title":"t","status":{{(int)status}},"code":"{{code}}","correlationId":"c"}""");

        var result = await Publish(attempt);

        result.End.ShouldBe(ResultPublishEnd.Stopped);
        _storage.Puts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plain_http_storage_is_refused_unless_loopback_is_allowed()
    {
        var attempt = Attempt();
        WriteValidResult(attempt);
        _platform.Script(WorkOperation.ResultGrants, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"resultLocation":"results/r/j/a/","grants":[{"name":"facts.json","method":"PUT","url":"http://storage.test/x","requiredHeaders":{},"expiresAt":"2026-10-15T10:05:00Z"},{"name":"manifest.json","method":"PUT","url":"http://storage.test/y","requiredHeaders":{},"expiresAt":"2026-10-15T10:05:00Z"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        });

        (await Publish(attempt)).End.ShouldBe(ResultPublishEnd.Unavailable);
        _storage.Puts.ShouldBeEmpty();
    }

    [Fact]
    public void Contract_examples_match_their_expectations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "analysis", "result-manifest", "v1", "examples");
        var valid = Directory.GetFiles(Path.Combine(root, "valid"), "*.json");
        valid.ShouldNotBeEmpty();
        foreach (var file in valid)
        {
            ResultManifestSchema.IsValid(File.ReadAllBytes(file)).ShouldBeTrue(file);
        }

        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "invalid", "expectations.json")))!.AsObject();
        foreach (var (name, _) in expectations)
        {
            ResultManifestSchema.IsValid(File.ReadAllBytes(Path.Combine(root, "invalid", name))).ShouldBeFalse(name);
        }
    }
}
