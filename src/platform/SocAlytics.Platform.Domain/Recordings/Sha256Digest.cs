using System.Diagnostics.CodeAnalysis;

namespace SocAlytics.Platform.Domain.Recordings;

public sealed class Sha256Digest : IEquatable<Sha256Digest>
{
    public const string Prefix = "sha-256:";
    public const int ByteLength = 32;

    private readonly byte[] _bytes;

    private Sha256Digest(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static Sha256Digest FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A SHA-256 digest has exactly 32 bytes.", nameof(bytes));
        }

        return new Sha256Digest(bytes.ToArray());
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out Sha256Digest? digest)
    {
        digest = null;
        if (value is null
            || value.Length != Prefix.Length + ByteLength * 2
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = value.AsSpan(Prefix.Length);
        foreach (var c in hex)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        digest = new Sha256Digest(Convert.FromHexString(hex));
        return true;
    }

    public static Sha256Digest Parse(string value) =>
        TryParse(value, out var digest)
            ? digest
            : throw new FormatException("Expected 'sha-256:' followed by 64 lowercase hexadecimal characters.");

    public string ToBase64() => Convert.ToBase64String(_bytes);

    public override string ToString() => Prefix + Convert.ToHexStringLower(_bytes);

    public bool Equals(Sha256Digest? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as Sha256Digest);

    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
}
