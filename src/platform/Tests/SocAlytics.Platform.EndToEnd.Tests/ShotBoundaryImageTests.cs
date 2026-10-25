using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// The shot-boundary Analyst image carries the labels of its manifest, no model weights, and only the dependencies
/// its audit lists, and it completes a job under the Manager's restrictions (FR-009, FR-011, FR-013; SC-006).
/// </summary>
[Collection("EndToEnd")]
public sealed class ShotBoundaryImageTests
{
    private const string Prefix = "analysts/";
    private const string UnknownUser = "54321:54321";
    private const long MemoryLimitBytes = 2L * 1024 * 1024 * 1024;
    private const int ManifestExecutionTimeoutSeconds = 300;
    private const int EvidenceSegmentSeconds = 30;
    private const int LongClipSeconds = 60;
    private const string PeakMarker = "peak-memory-bytes=";

    // Runs the image's entry point and reports the container cgroup's peak memory (cgroup v2, then v1) on stderr.
    private const string RunWithPeakMemory = """
        /opt/media-context-classification/venv/bin/python -m media_context_classification
        code=$?
        peak=$(cat /sys/fs/cgroup/memory.peak 2>/dev/null || cat /sys/fs/cgroup/memory/memory.max_usage_in_bytes 2>/dev/null)
        echo "peak-memory-bytes=$peak" >&2
        exit $code
        """;
    private const string AuditPath = "src/analysts/capabilities/media-context-classification/audit/dependency-audit.json";

    private const string ListDistributions = """
        import importlib.metadata, json, re
        print(json.dumps({re.sub(r"[-_.]+", "-", d.metadata["Name"]).lower(): d.version for d in importlib.metadata.distributions()}))
        """;

    private const string FindModelWeights = """
        find / -xdev \( -path /proc -o -path /sys \) -prune -o -type f \
          \( -iname '*.onnx' -o -iname '*.pt' -o -iname '*.pth' -o -iname '*.safetensors' -o -iname '*.gguf' \
             -o -iname '*.ckpt' -o -iname '*.h5' -o -iname '*.tflite' -o -iname '*.pb' -o -iname '*.caffemodel' \
             -o -iname '*.weights' \) -print 2>/dev/null
        true
        """;

    [Fact]
    public async Task The_image_matches_its_manifest_and_audit_and_completes_a_job_under_the_Manager_restrictions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;

        var root = ShotBoundaryAnalystImage.FindRepositoryRoot();
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(root, ShotBoundaryAnalystImage.ManifestPath), ct);
        var manifest = JsonNode.Parse(manifestBytes)!;
        var audit = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, AuditPath), ct))!.AsArray();

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        try
        {
            image = await ShotBoundaryAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct);

            using var docker = new DockerEngine();
            await AssertLabelsAsync(docker, image.LocalName, manifest, manifestBytes, ct);

            using var runner = new ContainerRunner();
            await AssertNoModelWeightsAsync(runner, image.LocalName, ct);
            await AssertInstalledDistributionsAsync(runner, image.LocalName, audit, ct);
            await AssertJobCompletesAsync(runner, image.LocalName, ct);
            await AssertOneMinuteClipStaysWithinResourcesAsync(runner, image.LocalName, ct);
        }
        finally
        {
            if (image is not null)
            {
                await ShotBoundaryAnalystImage.RemoveAsync(image);
            }

            await registry.DisposeAsync();
        }
    }

    private static async Task AssertLabelsAsync(DockerEngine docker, string localName, JsonNode manifest, byte[] manifestBytes, CancellationToken ct)
    {
        var inspected = await docker.GetJsonAsync($"/images/{Uri.EscapeDataString(localName)}/json", ct);
        inspected.GetProperty("Architecture").GetString().ShouldBe("amd64");

        var config = inspected.GetProperty("Config");
        var user = config.GetProperty("User").GetString();
        user.ShouldNotBeNullOrEmpty();
        user.Split(':')[0].ShouldNotBe("0");
        user.ShouldNotBe("root");

        var labels = config.GetProperty("Labels");
        Label("io.socalytics.analyst-manifest.version").ShouldBe(manifest["manifestVersion"]!.GetValue<string>());
        Label("io.socalytics.analyst-manifest.sha256").ShouldBe(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        Label("io.socalytics.capability.id").ShouldBe(manifest["capability"]!["id"]!.GetValue<string>());
        Label("io.socalytics.capability.version").ShouldBe(manifest["capability"]!["version"]!.GetValue<string>());
        Label("io.socalytics.model.digest").ShouldBe(manifest["model"]!["digest"]!.GetValue<string>());
        Label("io.socalytics.model.digest").ShouldBe(ShotBoundaryAnalystImage.ModelDigest);

        string? Label(string name) => labels.GetProperty(name).GetString();
    }

    private static async Task AssertNoModelWeightsAsync(ContainerRunner runner, string image, CancellationToken ct)
    {
        var result = await runner.RunAsync(image, UnknownUser, ["/bin/sh", "-c"], [FindModelWeights], null, ct);
        result.ExitCode.ShouldBe(0, result.Stderr);
        result.Stdout.Trim().ShouldBeEmpty("The image contains files that look like model weights.");
    }

    private static async Task AssertInstalledDistributionsAsync(ContainerRunner runner, string image, JsonArray audit, CancellationToken ct)
    {
        var result = await runner.RunAsync(
            image, UnknownUser, ["/opt/media-context-classification/venv/bin/python", "-c"], [ListDistributions], null, ct);
        result.ExitCode.ShouldBe(0, result.Stderr);

        var installed = JsonDocument.Parse(result.Stdout).RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var audited = audit
            .Where(r => r!["scope"]!.GetValue<string>() == "runtime")
            .ToDictionary(r => Normalize(r!["name"]!.GetValue<string>()), r => r!["version"]!.GetValue<string>(), StringComparer.Ordinal);

        installed.OrderBy(p => p.Key, StringComparer.Ordinal).ShouldBe(
            audited.OrderBy(p => p.Key, StringComparer.Ordinal),
            "The Python distributions installed in the image differ from audit/dependency-audit.json.");
    }

    private static async Task AssertJobCompletesAsync(ContainerRunner runner, string image, CancellationToken ct)
    {
        using var media = CutMedia.Create();
        var mount = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-shot-boundary-mount-{Guid.NewGuid():N}");
        try
        {
            WriteMount(mount, media.ReadBytes(), CutMedia.DurationSeconds);

            var result = await runner.RunAsync(image, UnknownUser, null, null, mount, ct);
            result.ExitCode.ShouldBe(0, result.Stderr);
            result.Elapsed.TotalSeconds.ShouldBeLessThanOrEqualTo(
                EvidenceSegmentSeconds, "The few-second evidence segment took longer than the segment budget.");
            result.Changes.Where(p => p != "/socalytics").ShouldBeEmpty("The container wrote outside its mounts.");

            var facts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(mount, "result", "facts.json"), ct))
                .RootElement.GetProperty("facts").EnumerateArray()
                .Select(f => f.GetProperty("startMs").GetInt64())
                .ToList();
            facts.ShouldBe(CutMedia.ExpectedCuts.Select(c => c.MatchMs).ToList());
        }
        finally
        {
            try
            {
                Directory.Delete(mount, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task AssertOneMinuteClipStaysWithinResourcesAsync(ContainerRunner runner, string image, CancellationToken ct)
    {
        var clipDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-shot-boundary-clip-{Guid.NewGuid():N}");
        var mount = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-shot-boundary-long-mount-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(clipDirectory);
            var clip = Path.Combine(clipDirectory, "clip.mp4");
            await GenerateLongClipAsync(clip, ct);
            WriteMount(mount, await File.ReadAllBytesAsync(clip, ct), LongClipSeconds);

            var result = await runner.RunAsync(image, UnknownUser, ["/bin/sh", "-c"], [RunWithPeakMemory], mount, ct);
            result.ExitCode.ShouldBe(0, result.Stderr);
            result.Elapsed.TotalSeconds.ShouldBeLessThanOrEqualTo(
                ManifestExecutionTimeoutSeconds, "The one-minute 1080p clip exceeded the manifest's minimum execution timeout.");

            var peakLine = result.Stderr.Split('\n').LastOrDefault(l => l.StartsWith(PeakMarker, StringComparison.Ordinal));
            peakLine.ShouldNotBeNull($"The container's cgroup peak memory could not be read. stderr: {result.Stderr}");
            long.TryParse(peakLine[PeakMarker.Length..].Trim(), out var peak).ShouldBeTrue($"Unreadable peak memory: {peakLine}");
            peak.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThanOrEqualTo(MemoryLimitBytes, "The one-minute 1080p clip used more than 2 GiB of memory.");
        }
        finally
        {
            foreach (var directory in new[] { clipDirectory, mount })
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task GenerateLongClipAsync(string filePath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(SyntheticMedia.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate=25:duration={LongClipSeconds}",
            "-pix_fmt", "yuv420p", "-c:v", "libopenh264", "-b:v", "4M", "-g", "25", "-an", "-movflags", "+faststart",
            filePath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The media toolchain failed to start: '{SyntheticMedia.FfmpegPath}'.");
        var error = process.StandardError.ReadToEndAsync(ct);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await output;
        process.ExitCode.ShouldBe(0, $"ffmpeg failed to generate the one-minute clip: {await error}");
    }

    private static void WriteMount(string mount, byte[] mediaBytes, int durationSeconds)
    {
        var durationMs = durationSeconds * 1000L;
        const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        foreach (var directory in new[] { mount, Path.Combine(mount, "media"), Path.Combine(mount, "result"), Path.Combine(mount, "scratch") })
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, Open);
        }

        File.WriteAllBytes(Path.Combine(mount, "media", "segment.mp4"), mediaBytes);

        var manifest = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["identity"] = new JsonObject
            {
                ["recordingVersionId"] = "rv-0001",
                ["timelineMappingDigest"] = Digest("1"),
                ["segmentationPolicyDigest"] = Digest("2"),
                ["segmentNumber"] = 1,
            },
            ["segmentationPolicy"] = new JsonObject { ["version"] = 1, ["durationSeconds"] = durationSeconds },
            ["nominalWindow"] = Interval(),
            ["requestedSourceInterval"] = Interval(),
            ["encodedInterval"] = Interval(),
            ["timelineSpans"] = new JsonArray(new JsonObject { ["mediaStartMs"] = 0, ["mediaEndMs"] = durationMs, ["matchStartMs"] = 0 }),
            ["media"] = new JsonObject
            {
                ["artifactReference"] = "segment.mp4",
                ["mediaType"] = "video/mp4",
                ["digest"] = "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(mediaBytes)),
                ["sizeBytes"] = mediaBytes.Length,
            },
            ["producer"] = new JsonObject { ["toolchain"] = "ffmpeg test" },
            ["createdAt"] = "2026-10-20T10:00:00Z",
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(Path.Combine(mount, "media", "segment-manifest.json"), manifestBytes);

        var job = new JsonObject
        {
            ["contractVersion"] = "1.2.0",
            ["messageId"] = "msg-0001",
            ["readinessOccurrenceId"] = "ready-0001",
            ["readinessSequence"] = 1,
            ["runId"] = "run-0001",
            ["nodeId"] = "node-0001",
            ["nodeKey"] = "shot-boundary",
            ["logicalJobId"] = "job-0001",
            ["teamId"] = "team-0001",
            ["matchId"] = "match-0001",
            ["recordingSetVersionId"] = "rsv-0001",
            ["capability"] = new JsonObject
            {
                ["id"] = ShotBoundaryAnalystImage.CapabilityId,
                ["version"] = ShotBoundaryAnalystImage.CapabilityVersion,
            },
            ["tier"] = "low-level",
            ["schemaVersion"] = "1.0.0",
            ["executionScope"] = "segment",
            ["segment"] = new JsonObject
            {
                ["recordingSetVersionId"] = "rsv-0001",
                ["recordingVersionId"] = "rv-0001",
                ["timelineMappingDigest"] = Digest("1"),
                ["segmentationPolicyDigest"] = Digest("2"),
                ["segmentNumber"] = 1,
            },
            ["segmentWindow"] = new JsonObject { ["segmentId"] = "seg-0001", ["startMs"] = 0, ["endMs"] = durationMs },
            ["workflow"] = new JsonObject { ["id"] = "shot-boundary-workflow", ["version"] = "1.0.0" },
            ["inputs"] = new JsonArray(),
            ["pinned"] = new JsonObject
            {
                ["analystProfileId"] = ShotBoundaryAnalystImage.ProfileId,
                ["analystProfileDigest"] = Digest("3"),
                ["imageDigest"] = Digest("4"),
                ["modelDigest"] = ShotBoundaryAnalystImage.ModelDigest,
                ["policies"] = new JsonArray(new JsonObject { ["policyId"] = "retention", ["version"] = "1.0.0" }),
            },
            ["resources"] = new JsonObject { ["cpuMillicores"] = 1000, ["memoryMebibytes"] = 512, ["scratchDiskMebibytes"] = 256 },
            ["attemptBudget"] = new JsonObject { ["maxAttempts"] = 3 },
            ["timeouts"] = new JsonObject { ["leaseSeconds"] = 60, ["staleTimeoutSeconds"] = 120, ["executionTimeoutSeconds"] = 600 },
            ["createdAt"] = "2026-10-20T10:00:00Z",
            ["correlation"] = new JsonObject { ["correlationId"] = "corr-0001" },
            ["segmentMedia"] = new JsonObject
            {
                ["videoPolicy"] = "required",
                ["manifestChecksum"] = "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            },
        };
        File.WriteAllText(Path.Combine(mount, "job.json"), job.ToJsonString());
        File.WriteAllText(
            Path.Combine(mount, "attempt.json"),
            new JsonObject { ["contractVersion"] = "1.0.0", ["attemptId"] = "attempt-0001", ["attemptNumber"] = 1 }.ToJsonString());

        JsonObject Interval() => new() { ["startMs"] = 0, ["endMs"] = durationMs };
        static string Digest(string last) => "sha-256:" + new string('0', 63) + last;
    }

    private static string Normalize(string name) => System.Text.RegularExpressions.Regex.Replace(name, "[-_.]+", "-").ToLowerInvariant();

    /// <summary>Runs a container with the Manager's restrictions to its exit and returns its status, output, and file system changes.</summary>
    private sealed class ContainerRunner : IDisposable
    {
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token);
                return new NetworkStream(socket, ownsSocket: true);
            },
        })
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        /// <summary>Runs the image; without an entrypoint the image's own entry point runs against the bind-mounted <paramref name="mount"/>.</summary>
        public async Task<RunResult> RunAsync(string image, string user, string[]? entrypoint, string[]? command, string? mount, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                Image = image,
                User = user,
                Entrypoint = entrypoint,
                Cmd = command,
                HostConfig = new
                {
                    NetworkMode = "none",
                    ReadonlyRootfs = true,
                    CapDrop = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges" },
                    Binds = mount is null ? Array.Empty<string>() : [$"{mount}:/socalytics"],
                },
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var created = await _http.PostAsync("/containers/create", content, ct);
            created.EnsureSuccessStatusCode();
            var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("Id").GetString()!;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using var started = await _http.PostAsync($"/containers/{id}/start", null, ct);
                started.EnsureSuccessStatusCode();
                using var waited = await _http.PostAsync($"/containers/{id}/wait", null, ct);
                waited.EnsureSuccessStatusCode();
                var elapsed = stopwatch.Elapsed;
                var exitCode = JsonDocument.Parse(await waited.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("StatusCode").GetInt32();

                using var logs = await _http.GetAsync($"/containers/{id}/logs?stdout=1&stderr=1", ct);
                var (stdout, stderr) = Demultiplex(await logs.Content.ReadAsByteArrayAsync(ct));

                using var changed = await _http.GetAsync($"/containers/{id}/changes", ct);
                changed.EnsureSuccessStatusCode();
                var changes = JsonDocument.Parse(await changed.Content.ReadAsStringAsync(ct)).RootElement;
                var paths = changes.ValueKind == JsonValueKind.Array
                    ? changes.EnumerateArray().Select(c => c.GetProperty("Path").GetString()!).ToList()
                    : [];
                return new RunResult(exitCode, stdout, stderr, paths, elapsed);
            }
            finally
            {
                using var removed = await _http.DeleteAsync($"/containers/{id}?force=true", CancellationToken.None);
            }
        }

        public void Dispose() => _http.Dispose();

        // Docker multiplexes the streams of a container without a TTY as frames: type, three zero bytes, big-endian length.
        private static (string Stdout, string Stderr) Demultiplex(byte[] raw)
        {
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            var offset = 0;
            while (offset + 8 <= raw.Length)
            {
                var length = (raw[offset + 4] << 24) | (raw[offset + 5] << 16) | (raw[offset + 6] << 8) | raw[offset + 7];
                var count = Math.Min(length, raw.Length - offset - 8);
                (raw[offset] == 2 ? stderr : stdout).Write(raw, offset + 8, count);
                offset += 8 + length;
            }

            return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
        }
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr, IReadOnlyList<string> Changes, TimeSpan Elapsed);
}
