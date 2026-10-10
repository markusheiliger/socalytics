using System.Security.Cryptography;
using System.Text;
using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class TimelineMappingCanonicalFormTests
{
    private static TimelineSpanSeconds S(decimal start, decimal end, decimal match) => new(start, end, match);

    private static TimelineMapping Valid(params TimelineSpanSeconds[] spans)
    {
        TimelineMapping.TryCreate(spans, out var mapping, out var violations).ShouldBeTrue(string.Join("; ", violations));
        return mapping!;
    }

    private static string DigestOf(string json) =>
        "sha-256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void One_span_has_exact_canonical_form_and_digest()
    {
        var mapping = Valid(S(0m, 10.5m, 600m));

        const string expected =
            """{"spans":[{"matchStartMilliseconds":600000,"mediaEndMilliseconds":10500,"mediaStartMilliseconds":0}],"version":1}""";
        mapping.CanonicalJson.ShouldBe(expected);
        mapping.Digest.ToString().ShouldBe(DigestOf(expected));
        mapping.Spans.ShouldBe([new TimelineSpan(0, 10500, 600000)]);
    }

    [Fact]
    public void Two_spans_with_break_cut_out_have_exact_canonical_form()
    {
        var mapping = Valid(S(0m, 2700m, 0m), S(2700m, 5400.25m, 3600m));

        const string expected =
            """{"spans":[{"matchStartMilliseconds":0,"mediaEndMilliseconds":2700000,"mediaStartMilliseconds":0},{"matchStartMilliseconds":3600000,"mediaEndMilliseconds":5400250,"mediaStartMilliseconds":2700000}],"version":1}""";
        mapping.CanonicalJson.ShouldBe(expected);
        mapping.Digest.ToString().ShouldBe(DigestOf(expected));
    }

    [Fact]
    public void Identical_content_gives_identical_digest_and_changes_change_it()
    {
        var a = Valid(S(0m, 10m, 5m));
        Valid(S(0m, 10.000m, 5.0m)).Digest.ShouldBe(a.Digest);
        Valid(S(0m, 10.001m, 5m)).Digest.ShouldNotBe(a.Digest);
        Valid(S(0m, 10m, 5.001m)).Digest.ShouldNotBe(a.Digest);
        Valid(S(0.001m, 10m, 5m)).Digest.ShouldNotBe(a.Digest);
    }

    public static TheoryData<string, TimelineSpanSeconds[]> Invalid => new()
    {
        { "empty", [] },
        { "65 spans", Enumerable.Range(0, 65).Select(i => S(i * 2, i * 2 + 1, i * 2)).ToArray() },
        { "negative media start", [S(-1m, 1m, 0m)] },
        { "negative match start", [S(0m, 1m, -0.5m)] },
        { "sub-millisecond", [S(0m, 1.0005m, 0m)] },
        { "zero length", [S(1m, 1m, 0m)] },
        { "media overlap", [S(0m, 10m, 0m), S(9m, 20m, 100m)] },
        { "match overlap", [S(0m, 10m, 0m), S(10m, 20m, 5m)] },
        { "out of order", [S(10m, 20m, 10m), S(0m, 5m, 100m)] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_mappings_are_rejected(string name, TimelineSpanSeconds[] spans)
    {
        TimelineMapping.TryCreate(spans, out var mapping, out var violations).ShouldBeFalse(name);
        mapping.ShouldBeNull();
        violations.ShouldNotBeEmpty();
    }

    [Fact]
    public void Sixty_four_spans_are_accepted()
    {
        Valid(Enumerable.Range(0, 64).Select(i => S(i * 2, i * 2 + 1, i * 2)).ToArray()).Spans.Count.ShouldBe(64);
    }
}
