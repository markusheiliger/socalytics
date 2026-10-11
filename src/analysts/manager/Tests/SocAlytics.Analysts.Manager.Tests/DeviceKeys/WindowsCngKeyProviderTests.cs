using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

public sealed class WindowsCngKeyProviderTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Reference_round_trips_and_names_are_prefixed_and_unique()
    {
        var a = WindowsCngKeyReference.NewKeyName();
        var b = WindowsCngKeyReference.NewKeyName();
        a.ShouldStartWith("SocAlytics-AnalystManager-");
        a.ShouldNotBe(b);

        var reference = WindowsCngKeyReference.Format(WindowsCngKeyReference.PlatformProviderName, a);
        reference.Provider.ShouldBe("windows-cng");
        WindowsCngKeyReference.TryParse(reference, out var provider, out var name).ShouldBeTrue();
        provider.ShouldBe(WindowsCngKeyReference.PlatformProviderName);
        name.ShouldBe(a);
    }

    [Theory]
    [InlineData("pkcs11", "Microsoft Platform Crypto Provider|SocAlytics-AnalystManager-x")]
    [InlineData("windows-cng", "Other Provider|SocAlytics-AnalystManager-x")]
    [InlineData("windows-cng", "Microsoft Platform Crypto Provider|other-key")]
    [InlineData("windows-cng", "Microsoft Platform Crypto Provider|SocAlytics-AnalystManager-")]
    [InlineData("windows-cng", "garbage")]
    public void Malformed_references_are_rejected(string provider, string value) =>
        WindowsCngKeyReference.TryParse(new DeviceKeyReference(provider, value), out _, out _).ShouldBeFalse();

    [Fact(Skip = "Windows only", SkipUnless = nameof(IsWindows), SkipType = typeof(WindowsCngKeyProviderTests))]
    [SupportedOSPlatform("windows")]
    public async Task Create_sign_reopen_delete_and_export_is_refused()
    {
        var provider = new WindowsCngKeyProvider();
        var key = await provider.CreateKeyAsync(true, TestContext.Current.CancellationToken);
        try
        {
            var data = Encoding.ASCII.GetBytes("header.payload");
            var sig = key.SignEs256(data);
            sig.Length.ShouldBe(64);
            using var ecdsa = ECDsa.Create(key.PublicJwk.ToParameters());
            ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();

            WindowsCngKeyReference.TryParse(key.Reference, out var providerName, out var name).ShouldBeTrue();
            using (var cng = CngKey.Open(name, new CngProvider(providerName)))
            {
                var ex = Should.Throw<CryptographicException>(() => cng.Export(CngKeyBlobFormat.Pkcs8PrivateBlob));
                ex.HResult.ShouldBe(unchecked((int)0x80090029));
            }

            (await provider.OpenKeyAsync(key.Reference, TestContext.Current.CancellationToken)).Thumbprint.ShouldBe(key.Thumbprint);
        }
        finally
        {
            await provider.DeleteKeyAsync(key.Reference, TestContext.Current.CancellationToken);
        }

        var missing = await Should.ThrowAsync<DeviceKeyException>(() => provider.OpenKeyAsync(key.Reference, TestContext.Current.CancellationToken));
        missing.Code.ShouldBe("key-missing");
    }

    [Fact(Skip = "Windows only", SkipUnless = nameof(IsWindows), SkipType = typeof(WindowsCngKeyProviderTests))]
    [SupportedOSPlatform("windows")]
    public async Task Software_fallback_only_when_allowed()
    {
        var provider = new WindowsCngKeyProvider();
        if (provider.Protection != KeyProtection.Software)
        {
            return;
        }

        var ex = await Should.ThrowAsync<DeviceKeyException>(() => provider.CreateKeyAsync(false, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe("software-key-not-allowed");
    }
}
