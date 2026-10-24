using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

public sealed record SegmentManifestInput(
    MaterializedSegmentIdentity Identity,
    int PolicyVersion,
    int DurationSeconds,
    SegmentWindow Window,
    SegmentInterval EncodedInterval,
    string MediaArtifactReference,
    Sha256Digest MediaDigest,
    long MediaSizeBytes,
    string Toolchain,
    DateTimeOffset CreatedAt);

// CanonicalBytes are the stored manifest bytes; Checksum is their SHA-256.
public sealed record SegmentManifest(byte[] CanonicalBytes, Sha256Digest Checksum);

// Builds segment manifest 1.0.0 (contracts/segments/segment-manifest).
public sealed class SegmentManifestBuilder
{
    public const string ContractVersion = "1.0.0";
    public const string MediaType = "video/mp4";

    public SegmentManifest Build(SegmentManifestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Identity);
        ArgumentNullException.ThrowIfNull(input.Window);
        ArgumentNullException.ThrowIfNull(input.MediaDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.MediaArtifactReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Toolchain);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(input.PolicyVersion, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(input.DurationSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(input.MediaSizeBytes, 0);

        var spans = new JsonArray();
        foreach (var span in input.Window.TimelineSpans)
        {
            spans.Add(new JsonObject
            {
                ["mediaStartMs"] = span.MediaStartMilliseconds,
                ["mediaEndMs"] = span.MediaEndMilliseconds,
                ["matchStartMs"] = span.MatchStartMilliseconds,
            });
        }

        var manifest = new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["identity"] = new JsonObject
            {
                ["recordingVersionId"] = input.Identity.RecordingVersionId.ToString("D", CultureInfo.InvariantCulture),
                ["timelineMappingDigest"] = input.Identity.TimelineMappingDigest.ToString(),
                ["segmentationPolicyDigest"] = input.Identity.SegmentationPolicyDigest.ToString(),
                ["segmentNumber"] = input.Identity.SegmentNumber,
            },
            ["segmentationPolicy"] = new JsonObject
            {
                ["version"] = input.PolicyVersion,
                ["durationSeconds"] = input.DurationSeconds,
            },
            ["nominalWindow"] = Interval(input.Window.NominalWindow),
            ["requestedSourceInterval"] = Interval(input.Window.RequestedSourceInterval),
            ["encodedInterval"] = Interval(input.EncodedInterval),
            ["timelineSpans"] = spans,
            ["media"] = new JsonObject
            {
                ["artifactReference"] = input.MediaArtifactReference,
                ["mediaType"] = MediaType,
                ["digest"] = input.MediaDigest.ToString(),
                ["sizeBytes"] = input.MediaSizeBytes,
            },
            ["producer"] = new JsonObject { ["toolchain"] = input.Toolchain },
            ["createdAt"] = input.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

        var bytes = CanonicalJson.Serialize(manifest);
        return new SegmentManifest(bytes, Sha256Digest.FromBytes(SHA256.HashData(bytes)));
    }

    private static JsonObject Interval(SegmentInterval interval) => new()
    {
        ["startMs"] = interval.StartMilliseconds,
        ["endMs"] = interval.EndMilliseconds,
    };
}
