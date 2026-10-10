using System.Security.Cryptography;
using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class CompositeDigestTests
{
    private static readonly string Hex64 = new('a', 64);

    private static byte[] Content(long length, int seed)
    {
        var data = new byte[length];
        for (var i = 0L; i < length; i++)
        {
            data[i] = (byte)((i * 31 + seed) % 251);
        }

        return data;
    }

    [Fact]
    public void Sha256Digest_round_trips()
    {
        var text = "sha-256:" + Hex64;

        var digest = Sha256Digest.Parse(text);

        digest.ToString().ShouldBe(text);
        digest.Bytes.Length.ShouldBe(32);
        digest.ToBase64().ShouldBe(Convert.ToBase64String(Convert.FromHexString(Hex64)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha-256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha-256:abcd")]
    [InlineData("sha-512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha-256:gaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Sha256Digest_rejects_malformed(string? text)
    {
        Sha256Digest.TryParse(text, out var digest).ShouldBeFalse();
        digest.ShouldBeNull();
    }

    [Fact]
    public void Composite_round_trips_and_rejects_malformed()
    {
        var text = $"sha-256-parts:5242880:3:{Hex64}";

        var digest = CompositeContentDigest.Parse(text);

        digest.PartSizeBytes.ShouldBe(5242880);
        digest.PartCount.ShouldBe(3);
        digest.ToString().ShouldBe(text);

        foreach (var bad in new[]
                 {
                     null, "", $"sha-256:{Hex64}", $"sha-256-parts:0:3:{Hex64}", $"sha-256-parts:5:0:{Hex64}",
                     $"sha-256-parts:5:3:{Hex64.ToUpperInvariant()}", "sha-256-parts:5:3:abcd", $"sha-256-parts:-5:3:{Hex64}",
                 })
        {
            CompositeContentDigest.TryParse(bad, out _).ShouldBeFalse(bad);
        }
    }

    [Fact]
    public void Spike_vector_matches_hand_computed_digest()
    {
        const long partSize = 5_242_880;
        var parts = new[] { Content(partSize, 1), Content(partSize, 2), Content(1_234_567, 3) };
        var digests = parts.Select(p => Sha256Digest.FromBytes(SHA256.HashData(p))).ToList();

        var composite = CompositeContentDigest.FromPartDigests(partSize, digests);

        var concatenated = parts.SelectMany(p => SHA256.HashData(p)).ToArray();
        var expectedHex = Convert.ToHexStringLower(SHA256.HashData(concatenated));
        composite.ToString().ShouldBe($"sha-256-parts:5242880:3:{expectedHex}");
        composite.ToS3ChecksumValue().ShouldBe(Convert.ToBase64String(Convert.FromHexString(expectedHex)) + "-3");
        composite.MatchesS3Checksum(composite.ToS3ChecksumValue(), "COMPOSITE").ShouldBeTrue();
    }

    [Fact]
    public void Single_part_is_double_hash_and_differs_from_plain_digest()
    {
        var plain = SHA256.HashData(Content(1000, 7));

        var composite = CompositeContentDigest.FromPartDigests(1000, [Sha256Digest.FromBytes(plain)]);

        composite.ToString().ShouldBe($"sha-256-parts:1000:1:{Convert.ToHexStringLower(SHA256.HashData(plain))}");
        composite.ToString().ShouldNotContain(Convert.ToHexStringLower(plain));
    }

    [Fact]
    public void S3_form_normalization_rejects_mismatches()
    {
        var digests = new[] { Sha256Digest.Parse("sha-256:" + Hex64), Sha256Digest.Parse("sha-256:" + new string('b', 64)) };
        var composite = CompositeContentDigest.FromPartDigests(5_242_880, digests);
        var value = composite.ToS3ChecksumValue();
        var base64 = value[..value.LastIndexOf('-')];

        composite.MatchesS3Checksum(value, "COMPOSITE").ShouldBeTrue();
        composite.MatchesS3Checksum(base64 + "-3", "COMPOSITE").ShouldBeFalse();
        composite.MatchesS3Checksum(value, "FULL_OBJECT").ShouldBeFalse();
        composite.MatchesS3Checksum(value, null).ShouldBeFalse();
        composite.MatchesS3Checksum(null, "COMPOSITE").ShouldBeFalse();
        composite.MatchesS3Checksum(Convert.ToBase64String(SHA256.HashData([1])) + "-2", "COMPOSITE").ShouldBeFalse();
    }
}
