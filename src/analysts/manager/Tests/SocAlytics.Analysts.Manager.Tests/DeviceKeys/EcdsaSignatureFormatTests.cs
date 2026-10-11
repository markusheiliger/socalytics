using System.Security.Cryptography;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

public class EcdsaSignatureFormatTests
{
    [Fact]
    public void Der_and_raw_round_trip_for_real_signatures()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        for (var i = 0; i < 20; i++)
        {
            var data = RandomNumberGenerator.GetBytes(32);
            var der = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var raw = EcdsaSignatureFormat.DerToRaw(der);

            raw.Length.ShouldBe(64);
            ecdsa.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();
            ecdsa.VerifyData(data, EcdsaSignatureFormat.RawToDer(raw), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence).ShouldBeTrue();
        }
    }

    [Fact]
    public void Short_values_are_left_padded()
    {
        var raw = new byte[64];
        raw[31] = 1;
        raw[63] = 2;

        EcdsaSignatureFormat.DerToRaw(EcdsaSignatureFormat.RawToDer(raw)).ShouldBe(raw);
    }

    [Fact]
    public void Invalid_input_is_refused()
    {
        Should.Throw<FormatException>(() => EcdsaSignatureFormat.RawToDer(new byte[10]));
        Should.Throw<FormatException>(() => EcdsaSignatureFormat.DerToRaw([1, 2, 3]));
    }
}
