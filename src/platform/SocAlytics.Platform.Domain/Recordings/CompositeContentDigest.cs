using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace SocAlytics.Platform.Domain.Recordings;

public sealed class CompositeContentDigest : IEquatable<CompositeContentDigest>
{
    public const string Prefix = "sha-256-parts:";

    private readonly byte[] _bytes;

    private CompositeContentDigest(long partSizeBytes, int partCount, byte[] bytes)
    {
        PartSizeBytes = partSizeBytes;
        PartCount = partCount;
        _bytes = bytes;
    }

    public long PartSizeBytes { get; }

    public int PartCount { get; }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static CompositeContentDigest FromPartDigests(long partSizeBytes, IReadOnlyList<Sha256Digest> partDigests)
    {
        ArgumentNullException.ThrowIfNull(partDigests);
        ArgumentOutOfRangeException.ThrowIfLessThan(partSizeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(partDigests.Count, 1);

        var concatenated = new byte[partDigests.Count * Sha256Digest.ByteLength];
        for (var i = 0; i < partDigests.Count; i++)
        {
            partDigests[i].Bytes.CopyTo(concatenated.AsSpan(i * Sha256Digest.ByteLength));
        }

        return new CompositeContentDigest(partSizeBytes, partDigests.Count, SHA256.HashData(concatenated));
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out CompositeContentDigest? digest)
    {
        digest = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 3
            || !TryPositiveDecimal(parts[0], out var partSize)
            || !TryPositiveDecimal(parts[1], out var count)
            || count > int.MaxValue
            || parts[2].Length != Sha256Digest.ByteLength * 2)
        {
            return false;
        }

        foreach (var c in parts[2])
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        digest = new CompositeContentDigest(partSize, (int)count, Convert.FromHexString(parts[2]));
        return true;
    }

    public static CompositeContentDigest Parse(string value) =>
        TryParse(value, out var digest)
            ? digest
            : throw new FormatException("Expected 'sha-256-parts:<partSizeBytes>:<partCount>:<64 lowercase hex>'.");

    public string ToS3ChecksumValue() => Convert.ToBase64String(_bytes) + "-" + PartCount;

    public bool MatchesS3Checksum(string? checksum, string? checksumType)
    {
        if (string.IsNullOrEmpty(checksum) || !string.Equals(checksumType, "COMPOSITE", StringComparison.Ordinal))
        {
            return false;
        }

        var dash = checksum.LastIndexOf('-');
        if (dash <= 0 || !TryPositiveDecimal(checksum[(dash + 1)..], out var suffix) || suffix != PartCount)
        {
            return false;
        }

        var decoded = new byte[Sha256Digest.ByteLength];
        return Convert.TryFromBase64String(checksum[..dash], decoded, out var written)
            && written == decoded.Length
            && decoded.AsSpan().SequenceEqual(_bytes);
    }

    public override string ToString() =>
        $"{Prefix}{PartSizeBytes}:{PartCount}:{Convert.ToHexStringLower(_bytes)}";

    public bool Equals(CompositeContentDigest? other) =>
        other is not null
        && PartSizeBytes == other.PartSizeBytes
        && PartCount == other.PartCount
        && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as CompositeContentDigest);

    public override int GetHashCode() => HashCode.Combine(PartSizeBytes, PartCount, BitConverter.ToInt32(_bytes, 0));

    private static bool TryPositiveDecimal(string text, out long value)
    {
        value = 0;
        if (text.Length == 0 || text[0] == '0' || text.Length > 18)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        value = long.Parse(text, CultureInfo.InvariantCulture);
        return true;
    }
}
