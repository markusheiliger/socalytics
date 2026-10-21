#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Platform;

public class SegmentMediaClientTests
{
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly string ValidExample = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "contracts", "analysis", "segment-media", "v1", "examples", "valid", "segment-media.json"));

    private readonly FakePlatform _platform;
    private readonly PlatformClient _client;
    private readonly PlatformSession _session;

    public SegmentMediaClientTests()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T22:00:00Z"));
        _platform = new FakePlatform(time);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(time), new InactiveSignalSink());
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        _session = new PlatformSession(new Uri("https://platform.test"), key, "opaque-token");
    }

    private static string Problem(int status, string code) =>
        $$"""{"type":"urn:socalytics:problem:{{code}}","title":"t","status":{{status}},"code":"{{code}}","correlationId":"c"}""";

    [Fact]
    public async Task Request_shape_and_valid_response_are_read()
    {
        _platform.Script(WorkOperation.SegmentMediaRead, HttpStatusCode.OK, ValidExample);

        var result = await _client.GetSegmentMediaAsync(_session, AttemptId, 7);

        result.Kind.ShouldBe(WorkResultKind.Success);
        var sent = _platform.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.Uri.AbsolutePath.ShouldBe($"/api/v1/analysis/attempts/{AttemptId}/segment-media");
        sent.Uri.Query.ShouldBe("?fencingToken=7");
        _platform.Headers.Single().DPoP.ShouldNotBeNullOrEmpty();
        result.Value!.ContractVersion.ShouldBe("1.0.0");
        result.Value.ManifestChecksum.ShouldStartWith("sha-256:");
        result.Value.Manifest.GetProperty("media").GetProperty("artifactReference").GetString().ShouldBe("segment.mp4");
        result.Value.Grants.Select(g => g.Name).ShouldBe(["segment-manifest.json", "segment.mp4"]);
        result.Value.Grants.ShouldAllBe(g => g.Url.StartsWith("https://"));
        result.Value.Grants[0].ToString().ShouldNotContain("https://");
    }

    [Theory]
    [InlineData("missing-grants.json")]
    [InlineData("write-grant-name.json")]
    [InlineData("single-grant.json")]
    public async Task Response_invalid_against_the_contract_is_refused(string file)
    {
        var body = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "contracts", "analysis", "segment-media", "v1", "examples", "invalid", file));
        _platform.Script(WorkOperation.SegmentMediaRead, HttpStatusCode.OK, body);

        var result = await _client.GetSegmentMediaAsync(_session, AttemptId, 7);

        result.Kind.ShouldBe(WorkResultKind.Refused);
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Response_with_an_unknown_property_is_refused()
    {
        var node = JsonNode.Parse(ValidExample)!.AsObject();
        node["sourceLocation"] = "https://recordings.example.invalid/source.mp4";
        _platform.Script(WorkOperation.SegmentMediaRead, HttpStatusCode.OK, node.ToJsonString());

        (await _client.GetSegmentMediaAsync(_session, AttemptId, 7)).Kind.ShouldBe(WorkResultKind.Refused);
    }

    [Theory]
    [InlineData(409, "attempt-obsolete", WorkResultKind.Obsolete)]
    [InlineData(409, "segment-media-not-applicable", WorkResultKind.Conflict)]
    [InlineData(409, "segment-media-invalid", WorkResultKind.Conflict)]
    [InlineData(403, "attempt-foreign", WorkResultKind.Refused)]
    [InlineData(404, "attempt-not-found", WorkResultKind.Refused)]
    [InlineData(400, "validation-failed", WorkResultKind.Refused)]
    public async Task Refusals_are_mapped_like_the_upstream_read(int status, string code, WorkResultKind kind)
    {
        _platform.Script(WorkOperation.SegmentMediaRead, (HttpStatusCode)status, Problem(status, code));

        var result = await _client.GetSegmentMediaAsync(_session, AttemptId, 7);

        result.Kind.ShouldBe(kind);
        result.Code.ShouldBe(code);
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Unreachable_platform_is_transient()
    {
        _platform.Script(WorkOperation.SegmentMediaRead, () => throw new HttpRequestException("down"));

        (await _client.GetSegmentMediaAsync(_session, AttemptId, 7)).Kind.ShouldBe(WorkResultKind.Transient);
    }

    [Fact]
    public void Job_message_parses_segment_media()
    {
        var json = """
            {"messageId":"m","readinessOccurrenceId":"o","runId":"r","nodeId":"n","logicalJobId":"j","teamId":"t","matchId":"x",
             "capability":{"id":"c","version":"1.0.0"},"schemaVersion":"1.0.0","executionScope":"segment",
             "pinned":{"analystProfileDigest":"d","imageDigest":"i","modelDigest":null},
             "resources":{"cpuMillicores":1000,"memoryMebibytes":512},"correlation":{"correlationId":"c"},
             "segment":{"recordingVersionId":"rv","segmentNumber":1},
             "segmentMedia":{"videoPolicy":"optional","manifestChecksum":"sha-256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}}
            """;

        var job = JsonSerializer.Deserialize<AnalystJobMessage>(json, PlatformJson.Options)!;

        job.SegmentMedia.ShouldNotBeNull();
        job.SegmentMedia.VideoPolicy.ShouldBe("optional");
        job.SegmentMedia.ManifestChecksum.ShouldBe("sha-256:" + new string('d', 64));
    }

    [Fact]
    public void Job_message_without_segment_media_has_none()
    {
        var json = """
            {"messageId":"m","readinessOccurrenceId":"o","runId":"r","nodeId":"n","logicalJobId":"j","teamId":"t","matchId":"x",
             "capability":{"id":"c","version":"1.0.0"},"schemaVersion":"1.0.0","executionScope":"match",
             "pinned":{"analystProfileDigest":"d","imageDigest":"i"},
             "resources":{"cpuMillicores":1000,"memoryMebibytes":512},"correlation":{"correlationId":"c"}}
            """;

        JsonSerializer.Deserialize<AnalystJobMessage>(json, PlatformJson.Options)!.SegmentMedia.ShouldBeNull();
    }
}
