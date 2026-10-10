using System.Formats.Asn1;
using System.Numerics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public static class EcdsaSignatureFormat
{
    public const int RawLength = 64;

    public static byte[] DerToRaw(ReadOnlySpan<byte> der)
    {
        try
        {
            var seq = new AsnReader(der.ToArray(), AsnEncodingRules.DER).ReadSequence();
            var r = seq.ReadInteger();
            var s = seq.ReadInteger();
            seq.ThrowIfNotEmpty();
            var raw = new byte[RawLength];
            Write(r, raw.AsSpan(0, 32));
            Write(s, raw.AsSpan(32, 32));
            return raw;
        }
        catch (Exception e) when (e is AsnContentException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Invalid DER signature.", e);
        }
    }

    public static byte[] RawToDer(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLength)
        {
            throw new FormatException("Raw signature must be 64 bytes.");
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(new BigInteger(raw[..32], isUnsigned: true, isBigEndian: true));
            writer.WriteInteger(new BigInteger(raw[32..], isUnsigned: true, isBigEndian: true));
        }

        return writer.Encode();
    }

    private static void Write(BigInteger value, Span<byte> destination)
    {
        if (value.Sign < 0 || value.GetByteCount(isUnsigned: true) > destination.Length)
        {
            throw new FormatException("Invalid DER signature.");
        }

        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        destination.Clear();
        bytes.CopyTo(destination[(destination.Length - bytes.Length)..]);
    }
}
