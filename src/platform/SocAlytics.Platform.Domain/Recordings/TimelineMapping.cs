using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocAlytics.Platform.Domain.Recordings;

public readonly record struct TimelineSpanSeconds(
    decimal MediaStartSeconds,
    decimal MediaEndSeconds,
    decimal MatchStartSeconds);

public sealed class TimelineMapping
{
    public const int MinSpans = 1;
    public const int MaxSpans = 64;

    private TimelineMapping(IReadOnlyList<TimelineSpan> spans, string canonicalJson, Sha256Digest digest)
    {
        Spans = spans;
        CanonicalJson = canonicalJson;
        Digest = digest;
    }

    public IReadOnlyList<TimelineSpan> Spans { get; }

    public string CanonicalJson { get; }

    public Sha256Digest Digest { get; }

    public static bool TryCreate(
        IReadOnlyList<TimelineSpanSeconds> spans,
        [NotNullWhen(true)] out TimelineMapping? mapping,
        out IReadOnlyList<string> violations)
    {
        mapping = null;
        var errors = new List<string>();
        violations = errors;

        if (spans.Count < MinSpans || spans.Count > MaxSpans)
        {
            errors.Add($"A timeline mapping has {MinSpans} to {MaxSpans} spans.");
            return false;
        }

        var converted = new List<TimelineSpan>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (TryToMilliseconds(s.MediaStartSeconds, out var mediaStart)
                & TryToMilliseconds(s.MediaEndSeconds, out var mediaEnd)
                & TryToMilliseconds(s.MatchStartSeconds, out var matchStart))
            {
                converted.Add(new TimelineSpan(mediaStart, mediaEnd, matchStart));
            }
            else
            {
                errors.Add($"Span {i}: values must be nonnegative with at most millisecond precision.");
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        for (var i = 0; i < converted.Count; i++)
        {
            var span = converted[i];
            if (span.MediaEndMilliseconds <= span.MediaStartMilliseconds)
            {
                errors.Add($"Span {i}: mediaEnd must be greater than mediaStart.");
            }

            if (i > 0)
            {
                var prev = converted[i - 1];
                if (span.MediaStartMilliseconds < prev.MediaEndMilliseconds)
                {
                    errors.Add($"Span {i}: spans must be increasing and non-overlapping in media time.");
                }

                if (span.MatchStartMilliseconds
                    < prev.MatchStartMilliseconds + (prev.MediaEndMilliseconds - prev.MediaStartMilliseconds))
                {
                    errors.Add($"Span {i}: spans must be increasing and non-overlapping in match time.");
                }
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        var json = WriteCanonicalJson(converted);
        var digest = Sha256Digest.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        mapping = new TimelineMapping(converted.AsReadOnly(), json, digest);
        return true;
    }

    private static bool TryToMilliseconds(decimal seconds, out long milliseconds)
    {
        milliseconds = 0;
        if (seconds < 0 || seconds > long.MaxValue / 1000m)
        {
            return false;
        }

        var scaled = seconds * 1000m;
        if (scaled != decimal.Truncate(scaled))
        {
            return false;
        }

        milliseconds = (long)scaled;
        return true;
    }

    private static string WriteCanonicalJson(IReadOnlyList<TimelineSpan> spans)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("spans");
            foreach (var span in spans)
            {
                writer.WriteStartObject();
                writer.WriteNumber("matchStartMilliseconds", span.MatchStartMilliseconds);
                writer.WriteNumber("mediaEndMilliseconds", span.MediaEndMilliseconds);
                writer.WriteNumber("mediaStartMilliseconds", span.MediaStartMilliseconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("version", 1);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
