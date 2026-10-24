using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>One shot boundary the shot-boundary Analyst must report: the first frame of a new shot.</summary>
internal sealed record ExpectedCut(int SegmentNumber, long MatchMs, string Rule);

/// <summary>
/// A 15-second, 25 fps H.264 recording of solid-color shots with known hard cuts, generated at test time with the
/// configured <c>ffmpeg</c> in a test-owned directory (no binary fixture is committed). With 5-second segments the
/// cuts sit in segments 1 and 3, the cut at 10 040 ms is exactly on the second frame of segment 3, no cut sits on a
/// segment edge, and segment 2 is cut-free.
/// </summary>
internal sealed class CutMedia : IDisposable
{
    public const int DurationSeconds = 15;
    public const int FramesPerSecond = 25;
    public const int SegmentSeconds = 5;
    public const int SegmentCount = DurationSeconds / SegmentSeconds;
    public const int Width = 128;
    public const int Height = 72;
    public const string HardCutRule = "hard-cut";

    // Shot colors differ strongly in every channel, so every cut is a hard cut and every shot is static.
    private static readonly (int Frames, string Color)[] Shots =
    [
        (50, "0xC81E1E"),
        (40, "0x1EC81E"),
        (161, "0x1E1EC8"),
        (59, "0xE6E632"),
        (65, "0x303030"),
    ];

    private readonly string directory;

    private CutMedia(string directory, string filePath)
    {
        this.directory = directory;
        FilePath = filePath;
    }

    /// <summary>The exact cuts in match-time order; the match time equals the media time (single timeline span).</summary>
    public static IReadOnlyList<ExpectedCut> ExpectedCuts { get; } = BuildExpectedCuts();

    public static IReadOnlyList<ExpectedCut> CutsInSegment(int segmentNumber) =>
        [.. ExpectedCuts.Where(c => c.SegmentNumber == segmentNumber)];

    public string FilePath { get; }

    public byte[] ReadBytes() => File.ReadAllBytes(FilePath);

    /// <summary>Creates the media file; fails visibly with a named message when the toolchain is missing or unusable.</summary>
    public static CutMedia Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"socalytics-cut-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "cuts.mp4");
        try
        {
            Generate(SyntheticMedia.FfmpegPath, filePath);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return new CutMedia(directory, filePath);
    }

    public async Task<UploadedSyntheticRecording> UploadAndFinalizeAsync(
        ApiSession session, Guid matchId, CancellationToken cancellationToken)
    {
        var bytes = ReadBytes();
        var recording = new TestRecording(bytes.Length, [bytes]);
        using var storage = new HttpClient();

        var started = await SendAsync(
            session, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), HttpStatusCode.Created, cancellationToken);
        var sessionId = started.GetProperty("session").GetProperty("id").GetString()!;
        var grant = started.GetProperty("grants").GetProperty("parts").EnumerateArray().Single();
        using (var put = await RecordingTestData.PutPartAsync(storage, grant, bytes, cancellationToken))
        {
            put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(cancellationToken));
        }

        var completed = await SendAsync(
            session,
            $"/api/v1/matches/{matchId}/upload-sessions/{sessionId}/completion",
            new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = DurationSeconds, matchStartSeconds = 0 } } } },
            HttpStatusCode.Created,
            cancellationToken);
        var versionId = completed.GetProperty("recordingVersion").GetProperty("id").GetGuid();
        var mappingId = completed.GetProperty("timelineMapping").GetProperty("id").GetGuid();

        var members = new[] { new { recordingVersionId = versionId, timelineMappingId = mappingId } };
        var set = await SendAsync(
            session, $"/api/v1/matches/{matchId}/recording-set-versions", new { members }, HttpStatusCode.Created, cancellationToken);

        return new UploadedSyntheticRecording(
            versionId, mappingId, set.GetProperty("id").GetGuid(), recording.ExpectedContentDigest, recording.TotalSize);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<ExpectedCut> BuildExpectedCuts()
    {
        var cuts = new List<ExpectedCut>();
        var frame = 0;
        foreach (var (frames, _) in Shots)
        {
            if (frame > 0)
            {
                var matchMs = frame * 1000L / FramesPerSecond;
                cuts.Add(new ExpectedCut((int)(matchMs / (SegmentSeconds * 1000L)) + 1, matchMs, HardCutRule));
            }

            frame += frames;
        }

        return cuts;
    }

    private static string[] FfmpegArguments(string filePath)
    {
        var inputs = new List<string>();
        var concat = string.Empty;
        for (var i = 0; i < Shots.Length; i++)
        {
            var (frames, color) = Shots[i];
            inputs.AddRange(["-f", "lavfi", "-i", $"color=c={color}:size={Width}x{Height}:rate={FramesPerSecond}:duration={frames / (double)FramesPerSecond:0.###}"]);
            concat += $"[{i}:v]";
        }

        var filter = $"{concat}concat=n={Shots.Length}:v=1:a=0,format=yuv420p[v]";
        return
        [
            "-hide_banner", "-loglevel", "error", "-y",
            .. inputs,
            "-filter_complex", filter, "-map", "[v]",
            "-frames:v", (DurationSeconds * FramesPerSecond).ToString(),
            "-r", FramesPerSecond.ToString(),
            "-c:v", "libopenh264", "-b:v", "2M", "-allow_skip_frames", "0", "-g", FramesPerSecond.ToString(), "-an",
            "-movflags", "+faststart",
            filePath,
        ];
    }

    private static void Generate(string ffmpegPath, string filePath)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in FfmpegArguments(filePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"The media toolchain is missing: ffmpeg was not found at '{ffmpegPath}'. Install the pinned FFmpeg from "
                + $".github/actions/environment-setup/analyst-media-toolchain.json or set {SyntheticMedia.FfmpegPathVariable}.",
                ex);
        }

        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"The media toolchain failed: ffmpeg at '{ffmpegPath}' did not finish within 60 seconds.");
            }

            if (process.ExitCode != 0 || !File.Exists(filePath))
            {
                throw new InvalidOperationException(
                    $"The media toolchain failed: ffmpeg at '{ffmpegPath}' exited with {process.ExitCode}: {error.GetAwaiter().GetResult()}");
            }
        }
    }

    private static async Task<JsonElement> SendAsync(
        ApiSession session, string path, object body, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await session.Client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
