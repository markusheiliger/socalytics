using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Segments;
using SocAlytics.Platform.Infrastructure.Segments;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class FfmpegSegmentExtractorTests : IDisposable
{
	private readonly SyntheticMedia media = SyntheticMedia.Create();
	private readonly string workingRoot = Path.Combine(Path.GetTempPath(), $"socalytics-extractor-tests-{Guid.NewGuid():N}");

	public void Dispose()
	{
		media.Dispose();
		if (Directory.Exists(workingRoot))
		{
			Directory.Delete(workingRoot, recursive: true);
		}
	}

	private static string FfprobePath =>
		Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe");

	private FfmpegSegmentExtractor Create(string? ffmpegPath = null, string? prefix = null, long maxBytes = 1024 * 1024)
	{
		var toolchain = Options.Create(new SegmentToolchainOptions
		{
			FfmpegPath = ffmpegPath ?? SyntheticMedia.FfmpegPath,
			FfprobePath = ffmpegPath is null ? FfprobePath : "ffprobe-missing",
			ExpectedVersionPrefix = prefix ?? string.Empty,
		});
		var materialization = Options.Create(new SegmentMaterializationOptions
		{
			WorkingDirectory = workingRoot,
			MaxSegmentBytes = maxBytes,
			ExecutionTimeout = TimeSpan.FromSeconds(60),
		});
		return new FfmpegSegmentExtractor(
			new ToolchainProbe(toolchain), toolchain, materialization, NullLogger<FfmpegSegmentExtractor>.Instance);
	}

	private Uri Source => new(media.FilePath);

	[Fact]
	public async Task Extract_covers_the_requested_interval_and_starts_at_a_keyframe()
	{
		var extractor = Create();

		await using var segment = await extractor.ExtractAsync(Source, new SegmentInterval(2500, 6000), CancellationToken.None);

		segment.EncodedInterval.StartMilliseconds.ShouldBe(2000);
		segment.EncodedInterval.EndMilliseconds.ShouldBeGreaterThanOrEqualTo(6000);
		segment.SizeBytes.ShouldBe(new FileInfo(segment.MediaFilePath).Length);
		segment.Toolchain.ShouldStartWith("ffmpeg ");
	}

	[Fact]
	public async Task Extract_keeps_only_one_video_stream_without_metadata()
	{
		var extractor = Create();

		await using var segment = await extractor.ExtractAsync(Source, new SegmentInterval(1000, 4000), CancellationToken.None);

		var streams = Run(FfprobePath, $"-v error -show_entries stream=codec_type -of csv=p=0 \"{segment.MediaFilePath}\"");
		streams.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(["video"]);
	}

	[Fact]
	public async Task Extract_removes_the_working_directory_on_disposal()
	{
		var extractor = Create();

		var segment = await extractor.ExtractAsync(Source, new SegmentInterval(1000, 4000), CancellationToken.None);
		var directory = Path.GetDirectoryName(segment.MediaFilePath)!;
		Directory.Exists(directory).ShouldBeTrue();

		await segment.DisposeAsync();
		Directory.Exists(directory).ShouldBeFalse();
	}

	[Fact]
	public async Task Extract_fails_when_the_encoded_interval_does_not_cover_the_request()
	{
		var extractor = Create();

		var failure = await Should.ThrowAsync<SegmentExtractionException>(
			() => extractor.ExtractAsync(Source, new SegmentInterval(8000, 20000), CancellationToken.None));

		failure.Reason.ShouldBe(SegmentFailureReasons.CoverageIncomplete);
		failure.Transient.ShouldBeFalse();
		Directory.GetDirectories(workingRoot).ShouldBeEmpty();
	}

	[Fact]
	public async Task Extract_fails_when_the_segment_exceeds_the_size_limit()
	{
		var extractor = Create(maxBytes: 100);

		var failure = await Should.ThrowAsync<SegmentExtractionException>(
			() => extractor.ExtractAsync(Source, new SegmentInterval(1000, 4000), CancellationToken.None));

		failure.Reason.ShouldBe(SegmentFailureReasons.TooLarge);
		Directory.GetDirectories(workingRoot).ShouldBeEmpty();
	}

	[Fact]
	public async Task Extract_reports_the_toolchain_unavailable_when_the_executable_is_missing()
	{
		var extractor = Create(ffmpegPath: Path.Combine(workingRoot, "no-such-ffmpeg"));

		var failure = await Should.ThrowAsync<SegmentExtractionException>(
			() => extractor.ExtractAsync(Source, new SegmentInterval(1000, 4000), CancellationToken.None));

		failure.Reason.ShouldBe(SegmentFailureReasons.ToolchainUnavailable);
		failure.Transient.ShouldBeFalse();
	}

	[Fact]
	public async Task Extract_reports_the_toolchain_unavailable_when_the_version_differs()
	{
		var extractor = Create(prefix: "0.0.");

		var failure = await Should.ThrowAsync<SegmentExtractionException>(
			() => extractor.ExtractAsync(Source, new SegmentInterval(1000, 4000), CancellationToken.None));

		failure.Reason.ShouldBe(SegmentFailureReasons.ToolchainUnavailable);
	}

	private static string Run(string path, string arguments)
	{
		using var process = Process.Start(new ProcessStartInfo(path, arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return output;
	}
}
