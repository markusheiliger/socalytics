using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings;

namespace SocAlytics.Platform.Integration.Tests;

/// <summary>The span layout of the timeline mapping of a synthetic recording.</summary>
internal enum SyntheticTimeline
{
	/// <summary>Media 0-10 s maps to match 0-10 s.</summary>
	SingleSpan,

	/// <summary>Media 0-5 s maps to match 0-5 s and media 5-10 s maps to match 10-15 s (a match-time gap).</summary>
	TwoSpans,
}

internal sealed record UploadedSyntheticRecording(
	Guid RecordingVersionId, Guid TimelineMappingId, Guid RecordingSetVersionId, string ContentDigest, long SizeBytes);

/// <summary>
/// A 10-second, 64x64, keyframe-every-second MPEG-4 MP4 (<c>testsrc2</c>) created at test time with the configured
/// <c>ffmpeg</c> in a test-owned directory, so no binary fixture is committed.
/// </summary>
internal sealed class SyntheticMedia : IDisposable
{
	public const int DurationSeconds = 10;

	public const string FfmpegPathVariable = "Segments__Toolchain__FfmpegPath";

	private static readonly string[] FfmpegArguments =
	[
		"-hide_banner", "-loglevel", "error", "-y",
		"-f", "lavfi", "-i", "testsrc2=size=64x64:rate=25:duration=10",
		"-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-g", "25", "-bf", "0", "-an",
		"-movflags", "+faststart",
	];

	private readonly string directory;

	private SyntheticMedia(string directory, string filePath)
	{
		this.directory = directory;
		FilePath = filePath;
	}

	public string FilePath { get; }

	public byte[] ReadBytes() => File.ReadAllBytes(FilePath);

	public static string FfmpegPath =>
		Environment.GetEnvironmentVariable(FfmpegPathVariable) is { Length: > 0 } configured ? configured : "ffmpeg";

	/// <summary>Creates the media file; fails visibly with a named message when the toolchain is missing or unusable.</summary>
	public static SyntheticMedia Create()
	{
		var directory = Path.Combine(Path.GetTempPath(), $"socalytics-synthetic-media-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		var filePath = Path.Combine(directory, "synthetic.mp4");
		try
		{
			Generate(FfmpegPath, filePath);
		}
		catch
		{
			Directory.Delete(directory, recursive: true);
			throw;
		}

		return new SyntheticMedia(directory, filePath);
	}

	public async Task<UploadedSyntheticRecording> UploadAndFinalizeAsync(
		ApiSession session, Guid matchId, SyntheticTimeline timeline, CancellationToken cancellationToken)
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
			new { timelineMapping = new { spans = Spans(timeline) } },
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

	public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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

	private static object[] Spans(SyntheticTimeline timeline) => timeline switch
	{
		SyntheticTimeline.SingleSpan => [new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 0 }],
		SyntheticTimeline.TwoSpans =>
		[
			new { mediaStartSeconds = 0, mediaEndSeconds = 5, matchStartSeconds = 0 },
			new { mediaStartSeconds = 5, mediaEndSeconds = 10, matchStartSeconds = 10 },
		],
		_ => throw new ArgumentOutOfRangeException(nameof(timeline)),
	};

	private static void Generate(string ffmpegPath, string filePath)
	{
		var startInfo = new ProcessStartInfo(ffmpegPath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in FfmpegArguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		startInfo.ArgumentList.Add(filePath);

		Process process;
		try
		{
			process = Process.Start(startInfo)!;
		}
		catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
		{
			throw new InvalidOperationException(
				$"The media toolchain is missing: ffmpeg was not found at '{ffmpegPath}'. Install the pinned FFmpeg from "
				+ $".github/actions/environment-setup/analyst-media-toolchain.json or set {FfmpegPathVariable}.",
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
