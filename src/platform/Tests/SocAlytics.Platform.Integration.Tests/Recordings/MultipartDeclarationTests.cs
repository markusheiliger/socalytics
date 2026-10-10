using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class MultipartDeclarationTests
{
    private const long MiB = 1024 * 1024;
    private static readonly MultipartBounds Bounds = new(5L * 1024 * 1024 * 1024 * 1024, 5 * MiB, 5L * 1024 * MiB, 10_000);
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    private static List<string> Digests(int n) => Enumerable.Repeat(Digest, n).ToList();

    private static IReadOnlyList<FieldViolation> Reject(long total, long part, IReadOnlyList<string>? digests, MultipartBounds? bounds = null)
    {
        MultipartDeclaration.TryCreate(total, part, digests, bounds ?? Bounds, out var d, out var v).ShouldBeFalse();
        d.ShouldBeNull();
        v.ShouldNotBeEmpty();
        return v;
    }

    [Fact]
    public void Single_part_is_valid_even_below_minimum_part_size()
    {
        MultipartDeclaration.TryCreate(10, 10, Digests(1), Bounds, out var d, out _).ShouldBeTrue();
        d!.PartCount.ShouldBe(1);
        d.PartSize(1).ShouldBe(10);
        d.ExpectedDigest.PartCount.ShouldBe(1);
    }

    [Fact]
    public void Three_part_declaration_computes_final_part_size_and_digest()
    {
        var total = 2 * 5 * MiB + 1_234_567;
        MultipartDeclaration.TryCreate(total, 5 * MiB, Digests(3), Bounds, out var d, out _).ShouldBeTrue();
        d!.PartCount.ShouldBe(3);
        d.PartSize(1).ShouldBe(5 * MiB);
        d.PartSize(3).ShouldBe(1_234_567);
        d.PartDigests.Count.ShouldBe(3);
        d.ExpectedDigest.ShouldBe(CompositeContentDigest.FromPartDigests(5 * MiB, d.PartDigests));
        Should.Throw<ArgumentOutOfRangeException>(() => d.PartSize(4));
    }

    [Fact]
    public void Exact_multiple_has_full_final_part()
    {
        MultipartDeclaration.TryCreate(10 * MiB, 5 * MiB, Digests(2), Bounds, out var d, out _).ShouldBeTrue();
        d!.PartSize(2).ShouldBe(5 * MiB);
    }

    [Fact]
    public void Rejects_empty_recording() => Reject(0, 5 * MiB, Digests(1)).ShouldContain(v => v.Field == "totalSizeBytes");

    [Fact]
    public void Rejects_above_maximum_size() =>
        Reject(Bounds.MaxObjectSizeBytes + 1, Bounds.MaxPartSizeBytes, Digests(1)).ShouldContain(v => v.Field == "totalSizeBytes");

    [Fact]
    public void Rejects_non_final_part_below_minimum() =>
        Reject(11 * MiB, 5 * MiB - 1, Digests(3)).ShouldContain(v => v.Field == "partSizeBytes");

    [Fact]
    public void Rejects_part_above_maximum() =>
        Reject(10 * MiB, Bounds.MaxPartSizeBytes + 1, Digests(1)).ShouldContain(v => v.Field == "partSizeBytes");

    [Fact]
    public void Rejects_above_maximum_part_count() =>
        Reject(10 * MiB, 5 * MiB, Digests(2), Bounds with { MaxPartCount = 1 }).ShouldContain(v => v.Field == "partSizeBytes");

    [Fact]
    public void Rejects_digest_count_inconsistent_with_total() =>
        Reject(11 * MiB, 5 * MiB, Digests(2)).ShouldContain(v => v.Field == "partDigests");

    [Fact]
    public void Rejects_missing_digests() => Reject(10, 10, null).ShouldContain(v => v.Field == "partDigests");

    [Theory]
    [InlineData("sha-256:ABCDEF")]
    [InlineData("sha-256:")]
    [InlineData("md5:aaaa")]
    public void Rejects_malformed_digest(string bad) =>
        Reject(10 * MiB, 5 * MiB, [Digest, bad]).ShouldContain(v => v.Field == "partDigests[1]");

    [Fact]
    public void Descriptor_trims_and_accepts_valid_values()
    {
        RecordingDescriptor.TryCreate("  Half 1 ", null, "video/mp4", out var d, out _).ShouldBeTrue();
        d!.DisplayName.ShouldBe("Half 1");
        d.Description.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "x", "video/mp4", "displayName")]
    [InlineData("   ", "x", "video/mp4", "displayName")]
    [InlineData("n", null, "", "contentType")]
    public void Descriptor_rejects_invalid(string name, string? description, string type, string field)
    {
        RecordingDescriptor.TryCreate(name, description, type, out var d, out var v).ShouldBeFalse();
        d.ShouldBeNull();
        v.ShouldContain(x => x.Field == field);
    }

    [Fact]
    public void Descriptor_enforces_length_limits()
    {
        RecordingDescriptor.TryCreate(new string('n', 200), new string('d', 2000), "video/mp4", out _, out _).ShouldBeTrue();
        RecordingDescriptor.TryCreate(new string('n', 201), null, "video/mp4", out _, out var v1).ShouldBeFalse();
        v1.ShouldContain(x => x.Field == "displayName");
        RecordingDescriptor.TryCreate("n", new string('d', 2001), "video/mp4", out _, out var v2).ShouldBeFalse();
        v2.ShouldContain(x => x.Field == "description");
    }
}
