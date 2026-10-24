#pragma warning disable xUnit1051 // The fake platform and storage answer synchronously; there is nothing to cancel.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Work;

/// <summary>User Story 2 at the Manager level: segment media is staged, verified, and bounded before the Analyst launches.</summary>
public sealed class SegmentMediaStagerTests : IDisposable
{
    private static readonly string ManifestExample = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "contracts", "segments", "segment-manifest", "v1", "examples", "valid", "segment-manifest.json"));

    private readonly Guid _attemptId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T22:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly StubStorage _storage = new();
    private readonly PlatformSession _session;
    private readonly StagingOptions _options = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "socalytics-media-stager-test-" + Guid.NewGuid().ToString("N"));
    private byte[] _media = Encoding.UTF8.GetBytes("not really an mp4");
    private byte[] _manifest = [];
    private string _pinned = string.Empty;
    private int _reads;

    public SegmentMediaStagerTests()
    {
        _platform = new FakePlatform(_time);
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        Prepare();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static string Digest(byte[] bytes) => "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void Prepare(Action<JsonObject>? change = null, long? declaredSize = null)
    {
        var manifest = JsonNode.Parse(ManifestExample)!.AsObject();
        manifest["media"]!["digest"] = Digest(_media);
        manifest["media"]!["sizeBytes"] = declaredSize ?? _media.Length;
        change?.Invoke(manifest);
        _manifest = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        _pinned = Digest(_manifest);
        _storage.Objects["/segment-manifest.json"] = _manifest;
        _storage.Objects["/segment.mp4"] = _media;
        _platform.Script(WorkOperation.SegmentMediaRead, () =>
        {
            _reads++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["contractVersion"] = "1.0.0",
                    ["attemptId"] = _attemptId.ToString(),
                    ["logicalJobId"] = "logical-job-0001",
                    ["manifestChecksum"] = _pinned,
                    ["manifest"] = JsonNode.Parse(_manifest),
                    ["grants"] = new JsonArray(Grant("segment-manifest.json"), Grant("segment.mp4")),
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        });
    }

    private static JsonObject Grant(string name) => new()
    {
        ["name"] = name,
        ["url"] = $"https://storage.test/{name}?sig=abc",
        ["expiresAt"] = "2026-10-17T22:10:00Z",
    };

    private ActiveAttempt Attempt(string? pinned = null)
    {
        var job = JsonSerializer.Deserialize<AnalystJobMessage>($$$"""
            {"messageId":"m","readinessOccurrenceId":"o","runId":"r","nodeId":"n","logicalJobId":"j","teamId":"t","matchId":"x",
             "capability":{"id":"c","version":"1.0.0"},"schemaVersion":"1.0.0","executionScope":"segment",
             "pinned":{"analystProfileDigest":"d","imageDigest":"i","modelDigest":null},
             "resources":{"cpuMillicores":1000,"memoryMebibytes":512},"correlation":{"correlationId":"c"},
             "segment":{"recordingVersionId":"rv","segmentNumber":1},
             "segmentMedia":{"videoPolicy":"required","manifestChecksum":"{{{pinned ?? _pinned}}}"}}
            """, PlatformJson.Options)!;
        var claim = new ClaimGranted(_attemptId, Guid.NewGuid(), Guid.NewGuid(), 1, 7, _time.GetUtcNow().AddSeconds(60), 60, 120, 600, false);
        return new ActiveAttempt(job, claim, _time.GetUtcNow());
    }

    private Task<StagingResult> StageAsync(ActiveAttempt? attempt = null, Func<PlatformSession?>? sessions = null, Func<string, long>? freeSpace = null)
    {
        var stager = new SegmentMediaStager(
            new PlatformClient(new HttpClient(_platform), new ProofFactory(_time)), new HttpClient(_storage), _time, _options, false,
            freeSpace: freeSpace);
        var task = stager.StageAsync(attempt ?? Attempt(), _directory, sessions ?? (() => _session), CancellationToken.None);
        return Advance(task);
    }

    private async Task<StagingResult> Advance(Task<StagingResult> task)
    {
        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private string MediaDirectory => Path.Combine(_directory, "media");

    private void ExpectFailed(StagingResult result, string reason, string category)
    {
        result.End.ShouldBe(StagingEnd.Failed);
        result.Reason.ShouldBe(reason);
        result.FailureCategory.ShouldBe(category);
        Directory.Exists(MediaDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Verified_media_is_staged_as_manifest_and_mp4()
    {
        var result = await StageAsync();

        result.End.ShouldBe(StagingEnd.Staged);
        File.ReadAllBytes(Path.Combine(MediaDirectory, "segment-manifest.json")).ShouldBe(_manifest);
        File.ReadAllBytes(Path.Combine(MediaDirectory, "segment.mp4")).ShouldBe(_media);
        _reads.ShouldBe(1);
    }

    [Fact]
    public async Task Job_without_segment_media_stages_nothing()
    {
        var job = Attempt().Job with { SegmentMedia = null };
        var attempt = new ActiveAttempt(
            job, new ClaimGranted(_attemptId, Guid.NewGuid(), Guid.NewGuid(), 1, 7, _time.GetUtcNow().AddSeconds(60), 60, 120, 600, false),
            _time.GetUtcNow());

        (await StageAsync(attempt)).End.ShouldBe(StagingEnd.Staged);

        _reads.ShouldBe(0);
        Directory.Exists(MediaDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Manifest_checksum_mismatch_fails_and_deletes_the_partial_staging()
    {
        _storage.Objects["/segment-manifest.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_manifest) + " ");

        ExpectFailed(await StageAsync(), StagingReasons.DigestMismatch, "input-unavailable");
        _storage.Requests.ShouldNotContain("/segment.mp4");
    }

    [Fact]
    public async Task Pinned_checksum_that_differs_from_the_platform_answer_fails()
    {
        ExpectFailed(await StageAsync(Attempt("sha-256:" + new string('9', 64))), StagingReasons.DigestMismatch, "input-unavailable");
        _storage.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_invalid_against_the_schema_fails()
    {
        var invalid = JsonNode.Parse(ManifestExample)!.AsObject();
        invalid["media"]!["digest"] = Digest(_media);
        invalid["media"]!["sizeBytes"] = _media.Length;
        invalid["unexpected"] = true;
        var bytes = Encoding.UTF8.GetBytes(invalid.ToJsonString());
        Prepare();
        _pinned = Digest(bytes);
        _storage.Objects["/segment-manifest.json"] = bytes;

        ExpectFailed(await StageAsync(), StagingReasons.DigestMismatch, "input-unavailable");
        _storage.Requests.ShouldNotContain("/segment.mp4");
    }

    [Fact]
    public async Task Media_digest_mismatch_fails_and_deletes_the_partial_staging()
    {
        _storage.Objects["/segment.mp4"] = Encoding.UTF8.GetBytes("tampered with equal len");

        ExpectFailed(await StageAsync(), StagingReasons.DigestMismatch, "input-unavailable");
    }

    [Fact]
    public async Task Media_size_mismatch_fails()
    {
        Prepare(declaredSize: _media.Length + 1);

        ExpectFailed(await StageAsync(), StagingReasons.DigestMismatch, "input-unavailable");
    }

    [Fact]
    public async Task Media_over_the_limit_fails_before_download_as_resource_exhausted()
    {
        _options.MaxBytesPerAttempt = StagingOptions.MinimumBytes;
        _media = new byte[StagingOptions.MinimumBytes];
        Prepare();

        ExpectFailed(await StageAsync(), StagingReasons.LimitExceeded, "resource-exhausted");
        _storage.Requests.ShouldNotContain("/segment.mp4");
    }

    [Fact]
    public async Task Media_counts_against_the_limit_together_with_staged_upstream_inputs()
    {
        _options.MaxBytesPerAttempt = StagingOptions.MinimumBytes;
        _media = new byte[StagingOptions.MinimumBytes - 4096];
        Prepare();
        var inputs = Path.Combine(_directory, "inputs");
        Directory.CreateDirectory(inputs);
        File.WriteAllBytes(Path.Combine(inputs, "facts.json"), new byte[4096]);

        ExpectFailed(await StageAsync(), StagingReasons.LimitExceeded, "resource-exhausted");
        Directory.Exists(inputs).ShouldBeTrue();
    }

    [Fact]
    public async Task Media_beyond_the_free_space_fails_as_resource_exhausted()
    {
        ExpectFailed(await StageAsync(freeSpace: _ => 1), StagingReasons.DiskFull, "resource-exhausted");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "segment-media-not-applicable")]
    [InlineData(HttpStatusCode.Conflict, "segment-media-invalid")]
    [InlineData(HttpStatusCode.Forbidden, "attempt-foreign")]
    public async Task Refused_read_fails_as_input_unavailable(HttpStatusCode status, string code)
    {
        _platform.Script(WorkOperation.SegmentMediaRead, status,
            $$"""{"type":"urn:socalytics:problem:{{code}}","title":"t","status":{{(int)status}},"code":"{{code}}","correlationId":"c"}""");

        ExpectFailed(await StageAsync(), StagingReasons.ReadRefused, "input-unavailable");
        _storage.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Obsolete_read_stops_without_a_report()
    {
        _platform.Script(WorkOperation.SegmentMediaRead, HttpStatusCode.Conflict,
            """{"type":"urn:socalytics:problem:attempt-obsolete","title":"t","status":409,"code":"attempt-obsolete","correlationId":"c"}""");

        var result = await StageAsync();

        result.End.ShouldBe(StagingEnd.Obsolete);
        Directory.Exists(MediaDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Inactive_registration_stops_without_a_report()
    {
        (await StageAsync(sessions: () => null)).End.ShouldBe(StagingEnd.Obsolete);
    }

    [Fact]
    public async Task Unavailable_storage_fails_after_retrying_until_the_deadline()
    {
        _storage.UnavailableResponses = int.MaxValue;

        ExpectFailed(await StageAsync(), StagingReasons.StorageUnavailable, "input-unavailable");
        _storage.Requests.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Unavailable_platform_fails_after_retrying_until_the_deadline()
    {
        _platform.Script(WorkOperation.SegmentMediaRead, () => throw new HttpRequestException("down"));

        ExpectFailed(await StageAsync(), StagingReasons.StorageUnavailable, "input-unavailable");
    }

    [Fact]
    public async Task Expired_grants_are_renewed_through_a_new_read()
    {
        _storage.Forbidden["/segment.mp4"] = 1;

        (await StageAsync()).End.ShouldBe(StagingEnd.Staged);

        _reads.ShouldBe(2);
        File.ReadAllBytes(Path.Combine(MediaDirectory, "segment.mp4")).ShouldBe(_media);
    }

    [Fact]
    public async Task Grants_that_keep_expiring_fail_as_read_refused()
    {
        _storage.Forbidden["/segment.mp4"] = 100;

        ExpectFailed(await StageAsync(), StagingReasons.ReadRefused, "input-unavailable");
        _reads.ShouldBe(4);
    }

    private sealed class StubStorage : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Objects { get; } = [];

        public Dictionary<string, int> Forbidden { get; } = [];

        public int UnavailableResponses { get; set; }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (UnavailableResponses > 0)
            {
                UnavailableResponses--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (Forbidden.TryGetValue(path, out var remaining) && remaining > 0)
            {
                Forbidden[path] = remaining - 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(Objects.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
