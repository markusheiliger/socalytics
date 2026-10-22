using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Analysis.Evidence;

public sealed class SegmentManifestOffsets
{
    private readonly IReadOnlyList<Span> _spans;

    private SegmentManifestOffsets(long encodedStartMs, string mediaType, string mediaDigest, long mediaSizeBytes,
        IReadOnlyList<Span> spans)
    {
        EncodedStartMs = encodedStartMs;
        MediaType = mediaType;
        MediaDigest = mediaDigest;
        MediaSizeBytes = mediaSizeBytes;
        _spans = spans;
    }

    public long EncodedStartMs { get; }

    public string MediaType { get; }

    public string MediaDigest { get; }

    public long MediaSizeBytes { get; }

    /// <summary>
    /// Validates the manifest document against the segment manifest contract and reads what the offsets need.
    /// Returns <see langword="null"/> when the document violates the contract or its timeline is inconsistent.
    /// </summary>
    public static SegmentManifestOffsets? TryParse(JsonElement manifest, IContractValidator validator)
    {
        if (!validator.Validate(ContractSchemaIds.SegmentManifestV1, manifest).IsValid)
        {
            return null;
        }

        var encoded = manifest.GetProperty("encodedInterval");
        var encodedStart = encoded.GetProperty("startMs").GetInt64();
        if (encoded.GetProperty("endMs").GetInt64() <= encodedStart)
        {
            return null;
        }

        var spans = new List<Span>();
        foreach (var span in manifest.GetProperty("timelineSpans").EnumerateArray())
        {
            var mediaStart = span.GetProperty("mediaStartMs").GetInt64();
            var mediaEnd = span.GetProperty("mediaEndMs").GetInt64();
            if (mediaEnd <= mediaStart)
            {
                return null;
            }

            spans.Add(new Span(mediaStart, mediaEnd, span.GetProperty("matchStartMs").GetInt64()));
        }

        if (spans.Count == 0)
        {
            return null;
        }

        var media = manifest.GetProperty("media");
        return new SegmentManifestOffsets(
            encodedStart,
            media.GetProperty("mediaType").GetString()!,
            media.GetProperty("digest").GetString()!,
            media.GetProperty("sizeBytes").GetInt64(),
            spans);
    }

    /// <summary>
    /// Translates the half-open match-time window <c>[fromMs, toMs)</c> to offsets on the media's playback timeline.
    /// Refuses a window that is empty or not inside one timeline span.
    /// </summary>
    public bool TryTranslate(long fromMs, long toMs, out long startOffsetMs, out long endOffsetMs)
    {
        startOffsetMs = 0;
        endOffsetMs = 0;
        if (toMs <= fromMs)
        {
            return false;
        }

        foreach (var span in _spans)
        {
            var spanEnd = span.MatchStartMs + (span.MediaEndMs - span.MediaStartMs);
            if (fromMs >= span.MatchStartMs && toMs <= spanEnd)
            {
                startOffsetMs = Offset(span, fromMs);
                endOffsetMs = Offset(span, toMs);
                return true;
            }
        }

        return false;
    }

    private long Offset(Span span, long matchMs) => span.MediaStartMs + (matchMs - span.MatchStartMs) - EncodedStartMs;

    private sealed record Span(long MediaStartMs, long MediaEndMs, long MatchStartMs);
}
