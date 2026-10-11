using System.Buffers.Binary;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public static class DeviceFingerprint
{
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";

    public static string Compute(PublicJwk jwk) => FromThumbprintHash(JwkThumbprint.ComputeHash(jwk));

    public static string FromThumbprintHash(ReadOnlySpan<byte> thumbprint)
    {
        var n = BinaryPrimitives.ReadUInt64BigEndian(thumbprint) % 25_600_000_000UL;
        Span<char> digits = stackalloc char[9];
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

        return new string(digits);
    }
}
