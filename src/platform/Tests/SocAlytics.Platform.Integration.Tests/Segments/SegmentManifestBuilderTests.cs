using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Shouldly;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentManifestBuilderTests
{
	private static readonly Guid RecordingVersionId = Guid.Parse("0f8e4a7c-1b2d-4c3e-9a5f-6d7e8f9a0b1c");
	private static readonly Sha256Digest MappingDigest = Sha256Digest.FromBytes(SHA256.HashData("mapping"u8));
	private static readonly Sha256Digest PolicyDigest = Sha256Digest.FromBytes(SHA256.HashData("policy"u8));
	private static readonly Sha256Digest MediaDigest = Sha256Digest.FromBytes(SHA256.HashData("media"u8));

	private static JsonSchema LoadSchema()
	{
		var root = Path.Combine(AppContext.BaseDirectory, "contracts");
		var registry = new SchemaRegistry { Fetch = (_, _) => null };
		var options = new BuildOptions { SchemaRegistry = registry };
		JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "common", "v1", "common.schema.json")), options);
		return JsonSchema.FromText(
			File.ReadAllText(Path.Combine(root, "segments", "segment-manifest", "v1", "segment-manifest.schema.json")),
			options);
	}

	private static SegmentManifestInput Input(params TimelineSpan[] spans)
	{
		var identity = new MaterializedSegmentIdentity(RecordingVersionId, MappingDigest, PolicyDigest, 2);
		var window = SegmentWindowCalculator.Calculate(2, 10, spans).Window!;
		return new SegmentManifestInput(
			identity,
			1,
			10,
			window,
			new SegmentInterval(window.RequestedSourceInterval.StartMilliseconds - 500, window.RequestedSourceInterval.EndMilliseconds),
			"segment.mp4",
			MediaDigest,
			1_048_576,
			"ffmpeg 7.1",
			new DateTimeOffset(2026, 10, 17, 22, 30, 0, TimeSpan.Zero));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Manifest_validates_against_the_schema(bool clipped)
	{
		var spans = clipped
			? new[] { new TimelineSpan(3_600_000, 3_604_000, 10_000), new TimelineSpan(3_700_000, 3_703_000, 16_000) }
			: [new TimelineSpan(3_600_000, 3_610_000, 10_000)];

		var manifest = new SegmentManifestBuilder().Build(Input(spans));

		var instance = JsonDocument.Parse(manifest.CanonicalBytes).RootElement;
		var result = LoadSchema().Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
		result.IsValid.ShouldBeTrue(JsonSerializer.Serialize(result.Details?.Where(d => !d.IsValid).Select(d => d.Errors)));

		instance.GetProperty("timelineSpans").GetArrayLength().ShouldBe(clipped ? 2 : 1);
		instance.GetProperty("nominalWindow").GetProperty("startMs").GetInt64().ShouldBe(10_000);
		instance.GetProperty("nominalWindow").GetProperty("endMs").GetInt64().ShouldBe(20_000);
		instance.GetProperty("media").GetProperty("mediaType").GetString().ShouldBe("video/mp4");
		instance.GetProperty("media").GetProperty("digest").GetString().ShouldBe(MediaDigest.ToString());
		instance.GetProperty("producer").GetProperty("toolchain").GetString().ShouldBe("ffmpeg 7.1");
		instance.GetProperty("createdAt").GetString().ShouldBe("2026-10-17T22:30:00.000Z");
		instance.GetProperty("identity").GetProperty("recordingVersionId").GetString().ShouldBe(RecordingVersionId.ToString("D"));
	}

	[Fact]
	public void Canonical_bytes_are_deterministic_and_checksummed()
	{
		var builder = new SegmentManifestBuilder();
		var input = Input(new TimelineSpan(3_600_000, 3_610_000, 10_000));

		var first = builder.Build(input);
		var second = builder.Build(input);

		second.CanonicalBytes.ShouldBe(first.CanonicalBytes);
		second.Checksum.ShouldBe(first.Checksum);
		first.Checksum.ShouldBe(Sha256Digest.FromBytes(SHA256.HashData(first.CanonicalBytes)));

		var text = Encoding.UTF8.GetString(first.CanonicalBytes);
		text.ShouldStartWith("{\"contractVersion\":\"1.0.0\",\"createdAt\":");
		text.ShouldNotContain("\n");
		text.ShouldNotContain("\": ");
		text.ShouldNotContain(", \"");
	}

	[Fact]
	public void Checksum_changes_with_the_media_digest()
	{
		var builder = new SegmentManifestBuilder();
		var input = Input(new TimelineSpan(3_600_000, 3_610_000, 10_000));

		var other = builder.Build(input with { MediaDigest = PolicyDigest });

		other.Checksum.ShouldNotBe(builder.Build(input).Checksum);
	}
}
