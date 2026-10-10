using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Platform.Domain.Registry;

public sealed class DeviceFingerprint
{
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";
    private const ulong Modulus = 25_600_000_000UL; // 20^8

    private DeviceFingerprint(string value) => Value = value;

    // Eight characters grouped as XXXX-XXXX.
    public string Value { get; }

    public override string ToString() => Value;

    public static DeviceFingerprint From(DeviceKeyThumbprint thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);
        var n = BinaryPrimitives.ReadUInt64BigEndian(thumbprint.Hash.AsSpan(0, 8)) % Modulus;
        var digits = new char[9];
        for (var i = 8; i >= 0; i--)
        {
            if (i == 4)
            {
                digits[i] = '-';
                continue;
            }

            digits[i] = Alphabet[(int)(n % 20)];
            n /= 20;
        }

        return new DeviceFingerprint(new string(digits));
    }

    public static DeviceFingerprint FromStored(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new DeviceFingerprint(value);
    }

    public bool Matches(string? entered)
    {
        var expected = Encoding.UTF8.GetBytes(Normalize(Value));
        var actual = Encoding.UTF8.GetBytes(Normalize(entered ?? string.Empty));
        // Length is not secret: every fingerprint has the same length.
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Normalize(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
