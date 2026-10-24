using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

// SC-001; FR-001 to FR-006 over HTTP against PostgreSQL, RustFS, and the pinned FFmpeg toolchain.
public sealed class EvidenceResolutionTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const long FrameMs = 40;

	private static readonly Dictionary<string, string?> ShortMaxDuration = new() { ["Analysis:Evidence:MaxDuration"] = "00:00:03" };

	private static object FactBody(int ordinal, long before = 0, long after = 0, Guid? resultId = null, EvidenceScenario? s = null) =>
		new { fact = new { acceptedResultId = resultId ?? s!.ResultId, ordinal, contextBeforeMs = before, contextAfterMs = after } };

	private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
		EvidenceScenario s, object body, CancellationToken ct)
	{
		using var response = await s.Coach.SendAsync(HttpMethod.Post, s.EvidencePath, body, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
	}

	// Resolves until every covered part is available; segments are materialized between rounds.
	private static async Task<JsonElement> ResolveAvailableAsync(EvidenceScenario s, object body, CancellationToken ct)
	{
		for (var round = 0; round < 4; round++)
		{
			var (status, resolution) = await SendAsync(s, body, ct);
			status.ShouldBe(HttpStatusCode.OK, resolution.ToString());
			var pending = resolution.GetProperty("parts").EnumerateArray()
				.Any(p => p.GetProperty("state").GetString() is "materializing" or "rematerializing");
			if (!pending)
			{
				return resolution;
			}

			await s.MaterializeSegmentsAsync(ct);
		}

		throw new InvalidOperationException("The evidence segments did not become available.");
	}

	private static JsonElement[] Parts(JsonElement resolution) => [.. resolution.GetProperty("parts").EnumerateArray()];

	private static void ShouldCoverWithoutGapOrOverlap(JsonElement resolution)
	{
		var requested = resolution.GetProperty("requested");
		var parts = Parts(resolution);
		parts[0].GetProperty("startMs").GetInt64().ShouldBe(requested.GetProperty("fromMs").GetInt64());
		parts[^1].GetProperty("endMs").GetInt64().ShouldBe(requested.GetProperty("toMs").GetInt64());
		for (var i = 1; i < parts.Length; i++)
		{
			parts[i].GetProperty("startMs").GetInt64().ShouldBe(parts[i - 1].GetProperty("endMs").GetInt64());
		}
	}

	private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
	{
		using var http = new HttpClient();
		return await http.GetByteArrayAsync(url, ct);
	}

	private static string[] FfprobePacketTimes(byte[] media)
	{
		var path = Path.Combine(Path.GetTempPath(), $"socalytics-evidence-{Guid.NewGuid():N}.mp4");
		File.WriteAllBytes(path, media);
		try
		{
			var ffprobe = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe");
			var info = new ProcessStartInfo(ffprobe, $"-v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 \"{path}\"")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(info)!;
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			process.ExitCode.ShouldBe(0, process.StandardError.ReadToEnd());
			return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static async Task ShouldMatchStoredMediaAsync(JsonElement media, CancellationToken ct)
	{
		var bytes = await DownloadAsync(media.GetProperty("url").GetString()!, ct);
		((long)bytes.Length).ShouldBe(media.GetProperty("mediaSizeBytes").GetInt64());
		media.GetProperty("mediaDigest").GetString().ShouldBe("sha-256:" + SyntheticMedia.Sha256Hex(bytes));

		var packets = FfprobePacketTimes(bytes)
			.Select(t => (long)Math.Round(double.Parse(t, CultureInfo.InvariantCulture) * 1000))
			.Order()
			.ToArray();
		// Offsets are relative to the encoded start of the segment, which is its first packet.
		packets = [.. packets.Select(t => t - packets[0])];
		var start = media.GetProperty("startOffsetMs").GetInt64();
		var end = media.GetProperty("endOffsetMs").GetInt64();
		packets.ShouldContain(t => Math.Abs(t - start) <= FrameMs, $"no packet within one frame of the start offset {start}: {string.Join(',', packets.Take(8))}..{packets[^1]}");
		packets.ShouldContain(t => Math.Abs(t - end) <= FrameMs, $"no packet within one frame of the end offset {end}");
		(packets[^1] + FrameMs).ShouldBeGreaterThanOrEqualTo(end - FrameMs);
		packets[0].ShouldBeLessThanOrEqualTo(start + FrameMs);
	}

	[Fact]
	public async Task Fact_inside_one_segment_is_one_part_whose_link_serves_the_attested_bytes_until_it_expires()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct, extraSettings: ShortMaxDuration);
		await s.MaterializeAllAsync(ct);

		var resolution = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.InsideFactOrdinal, s: s), ct);

		resolution.GetProperty("shortened").GetBoolean().ShouldBeFalse();
		var fact = resolution.GetProperty("fact");
		fact.GetProperty("startMs").GetInt64().ShouldBe(EvidenceScenario.InsideFactStartMs);
		fact.GetProperty("endMs").GetInt64().ShouldBe(EvidenceScenario.InsideFactEndMs);
		var part = Parts(resolution).ShouldHaveSingleItem();
		part.GetProperty("state").GetString().ShouldBe("available");
		part.GetProperty("startMs").GetInt64().ShouldBe(EvidenceScenario.InsideFactStartMs);
		part.GetProperty("endMs").GetInt64().ShouldBe(EvidenceScenario.InsideFactEndMs);
		var media = part.GetProperty("media");
		media.GetProperty("recordingVersionId").GetGuid().ShouldBe(s.Recordings[0].RecordingVersionId);
		(media.GetProperty("endOffsetMs").GetInt64() - media.GetProperty("startOffsetMs").GetInt64()).ShouldBe(1_000);
		await ShouldMatchStoredMediaAsync(media, ct);

		// A link that expires three seconds from now works at first and is refused afterwards.
		s.Factory.Time.Set(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(3));
		var shortLived = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.InsideFactOrdinal, s: s), ct);
		var url = Parts(shortLived).Single().GetProperty("media").GetProperty("url").GetString()!;
		using var http = new HttpClient();
		using (var before = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
		{
			before.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		await Task.Delay(TimeSpan.FromSeconds(5), ct);
		using var after = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		after.IsSuccessStatusCode.ShouldBeFalse();
	}

	[Fact]
	public async Task Fact_across_a_boundary_is_two_contiguous_parts_in_match_time_order()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct, extraSettings: ShortMaxDuration);
		await s.MaterializeAllAsync(ct);

		var resolution = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.BoundaryFactOrdinal, s: s), ct);

		var parts = Parts(resolution);
		parts.Length.ShouldBe(2);
		ShouldCoverWithoutGapOrOverlap(resolution);
		parts.Select(p => (p.GetProperty("startMs").GetInt64(), p.GetProperty("endMs").GetInt64()))
			.ShouldBe([(4_000L, 5_000L), (5_000L, 6_000L)]);
		parts.ShouldAllBe(p => p.GetProperty("state").GetString() == "available");
		var first = parts[0].GetProperty("media");
		var second = parts[1].GetProperty("media");
		first.GetProperty("mediaDigest").GetString().ShouldNotBe(second.GetProperty("mediaDigest").GetString());
		await ShouldMatchStoredMediaAsync(first, ct);
		await ShouldMatchStoredMediaAsync(second, ct);
	}

	[Fact]
	public async Task Context_extends_the_window_clips_at_zero_and_shortens_beyond_the_maximum_duration()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct, extraSettings: ShortMaxDuration);
		await s.MaterializeAllAsync(ct);

		var extended = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.InsideFactOrdinal, 500, 500, s: s), ct);
		extended.GetProperty("requested").GetProperty("fromMs").GetInt64().ShouldBe(500);
		extended.GetProperty("requested").GetProperty("toMs").GetInt64().ShouldBe(2_500);
		extended.GetProperty("shortened").GetBoolean().ShouldBeFalse();
		ShouldCoverWithoutGapOrOverlap(extended);

		var clipped = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.InsideFactOrdinal, 2_000, 0, s: s), ct);
		clipped.GetProperty("requested").GetProperty("fromMs").GetInt64().ShouldBe(0);
		clipped.GetProperty("requested").GetProperty("toMs").GetInt64().ShouldBe(2_000);
		clipped.GetProperty("shortened").GetBoolean().ShouldBeFalse();
		Parts(clipped)[0].GetProperty("startMs").GetInt64().ShouldBe(0);

		var shortened = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.BoundaryFactOrdinal, 1_000, 1_000, s: s), ct);
		shortened.GetProperty("shortened").GetBoolean().ShouldBeTrue();
		var requested = shortened.GetProperty("requested");
		(requested.GetProperty("toMs").GetInt64() - requested.GetProperty("fromMs").GetInt64()).ShouldBe(3_000);
		ShouldCoverWithoutGapOrOverlap(shortened);
	}

	[Fact]
	public async Task Range_request_resolves_and_invalid_requests_are_validation_problems()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct, extraSettings: ShortMaxDuration);
		await s.MaterializeAllAsync(ct);

		var range = await ResolveAvailableAsync(s, new { range = new { fromMs = 3_000, toMs = 6_000 } }, ct);
		range.TryGetProperty("fact", out _).ShouldBeFalse();
		Parts(range).Length.ShouldBe(2);
		ShouldCoverWithoutGapOrOverlap(range);

		var fact = new { acceptedResultId = s.ResultId, ordinal = 0 };
		object[] invalid =
		[
			new { },
			new { fact, range = new { fromMs = 0, toMs = 1_000 } },
			new { range = new { fromMs = -1, toMs = 1_000 } },
			new { range = new { fromMs = 1_000, toMs = 1_000 } },
			new { range = new { fromMs = 2_000, toMs = 1_000 } },
			new { range = new { fromMs = 0 } },
			new { range = new { fromMs = 0, toMs = 3_001 } },
			new { fact = new { acceptedResultId = s.ResultId, ordinal = -1 } },
			new { fact = new { acceptedResultId = s.ResultId, ordinal = 0, contextBeforeMs = -1 } },
			new { fact = new { acceptedResultId = s.ResultId, ordinal = 0, contextAfterMs = 10_001 } },
		];
		foreach (var body in invalid)
		{
			var (status, problem) = await SendAsync(s, body, ct);
			status.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body) + " " + problem);
		}

		var (missing, _) = await SendAsync(s, FactBody(99, s: s), ct);
		missing.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Two_recordings_resolve_to_the_first_recording_in_set_order()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct, secondRecording: true, extraSettings: ShortMaxDuration);
		await s.MaterializeSegmentsAsync(ct);

		var resolution = await ResolveAvailableAsync(s, FactBody(EvidenceScenario.BoundaryFactOrdinal, s: s), ct);

		var parts = Parts(resolution);
		parts.Length.ShouldBe(2);
		ShouldCoverWithoutGapOrOverlap(resolution);
		foreach (var part in parts)
		{
			part.GetProperty("state").GetString().ShouldBe("available");
			part.GetProperty("media").GetProperty("recordingVersionId").GetGuid().ShouldBe(s.Recordings[0].RecordingVersionId);
		}
	}
}
