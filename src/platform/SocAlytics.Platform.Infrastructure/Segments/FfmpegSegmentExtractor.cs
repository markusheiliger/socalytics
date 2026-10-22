using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Infrastructure.Segments;

internal sealed class FfmpegSegmentExtractor(
    ToolchainProbe probe,
    IOptions<SegmentToolchainOptions> toolchainOptions,
    IOptions<SegmentMaterializationOptions> materializationOptions,
    ILogger<FfmpegSegmentExtractor> logger) : ISegmentExtractor
{
    public async Task<ExtractedSegment> ExtractAsync(
        Uri sourceReadUrl, SegmentInterval requestedSourceInterval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceReadUrl);
        var toolchain = await probe.ProbeAsync(cancellationToken);
        var materialization = materializationOptions.Value;
        var tools = toolchainOptions.Value;

        var workingDirectory = Path.Combine(materialization.WorkingDirectory, Guid.NewGuid().ToString("N"));
        var directoryInfo = Directory.CreateDirectory(workingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            directoryInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        var mediaPath = Path.Combine(workingDirectory, "segment.mp4");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(materialization.ExecutionTimeout);

            try
            {
                await CopyAsync(tools.FfmpegPath, sourceReadUrl, requestedSourceInterval, mediaPath, materialization.MaxSegmentBytes, timeout.Token);
                var packets = await ProbePacketsAsync(tools.FfprobePath, mediaPath, timeout.Token);
                return Verify(workingDirectory, mediaPath, requestedSourceInterval, packets, materialization.MaxSegmentBytes, toolchain);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Segment extraction exceeded the execution timeout of {Timeout}.", materialization.ExecutionTimeout);
                throw new SegmentExtractionException(SegmentFailureReasons.ExtractionFailed, transient: true);
            }
        }
        catch
        {
            await new ExtractedSegment(workingDirectory, mediaPath, 0, default, toolchain).DisposeAsync();
            throw;
        }
    }

    private async Task CopyAsync(
        string ffmpegPath, Uri source, SegmentInterval interval, string mediaPath, long maxBytes, CancellationToken cancellationToken)
    {
        // The packet at exactly the requested end is kept, so the end bound is one millisecond later.
        var arguments = new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-ss", Seconds(interval.StartMilliseconds),
            "-copyts",
            "-i", source.AbsoluteUri,
            "-to", Seconds(interval.EndMilliseconds + 1),
            "-map", "0:v:0", "-c", "copy", "-an", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-fflags", "+bitexact", "-flags:v", "+bitexact",
            "-fs", (maxBytes + 1).ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            "-f", "mp4",
            mediaPath,
        };

        var result = await ToolProcess.RunAsync(ffmpegPath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            // The ffmpeg output can repeat the presigned URL, so only the exit code is logged.
            logger.LogWarning("ffmpeg exited with code {ExitCode} while extracting a segment.", result.ExitCode);
            throw new SegmentExtractionException(SegmentFailureReasons.ExtractionFailed, transient: true);
        }
    }

    private async Task<List<Packet>> ProbePacketsAsync(string ffprobePath, string mediaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath))
        {
            return [];
        }

        var result = await ToolProcess.RunAsync(
            ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,duration_time,flags", "-of", "csv=p=0", mediaPath],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            logger.LogWarning("ffprobe exited with code {ExitCode} for a written segment.", result.ExitCode);
            throw new SegmentExtractionException(SegmentFailureReasons.MediaInvalid, transient: false);
        }

        var packets = new List<Packet>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
            {
                throw new SegmentExtractionException(SegmentFailureReasons.MediaInvalid, transient: false);
            }

            var duration = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            packets.Add(new Packet(pts, duration, parts[2].Contains('K')));
        }

        return packets;
    }

    private static ExtractedSegment Verify(
        string workingDirectory, string mediaPath, SegmentInterval requested, List<Packet> packets, long maxBytes, string toolchain)
    {
        if (packets.Count == 0)
        {
            throw new SegmentExtractionException(SegmentFailureReasons.CoverageIncomplete, transient: false);
        }

        var size = new FileInfo(mediaPath).Length;
        if (size > maxBytes)
        {
            throw new SegmentExtractionException(SegmentFailureReasons.TooLarge, transient: false);
        }

        var first = packets.MinBy(static p => p.PtsSeconds)!;
        if (!first.IsKeyframe)
        {
            throw new SegmentExtractionException(SegmentFailureReasons.CoverageIncomplete, transient: false);
        }

        var encoded = new SegmentInterval(
            Milliseconds(first.PtsSeconds),
            Milliseconds(packets.Max(static p => p.PtsSeconds + p.DurationSeconds)));
        if (encoded.StartMilliseconds > requested.StartMilliseconds || encoded.EndMilliseconds < requested.EndMilliseconds)
        {
            throw new SegmentExtractionException(SegmentFailureReasons.CoverageIncomplete, transient: false);
        }

        return new ExtractedSegment(workingDirectory, mediaPath, size, encoded, toolchain);
    }

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000m).ToString("0.000", CultureInfo.InvariantCulture);

    private static long Milliseconds(double seconds) => (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);

    private sealed record Packet(double PtsSeconds, double DurationSeconds, bool IsKeyframe);
}
