using System.Runtime.Versioning;
using System.Security.Cryptography;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

public sealed class MacBridgeKeyProviderTests
{
    public static bool IsMacWithBridge => OperatingSystem.IsMacOS() && MacBridgeLibrary.IsPresent;

    [Theory]
    [InlineData(-1, ManagerDiagnosticCodes.NoSupportedKeyStore)]
    [InlineData(-2, ManagerDiagnosticCodes.KeyMissing)]
    [InlineData(-3, ManagerDiagnosticCodes.KeyInaccessible)]
    [InlineData(-5, ManagerDiagnosticCodes.SoftwareKeyNotAllowed)]
    [InlineData(-7, ManagerDiagnosticCodes.KeyInaccessible)]
    public void Status_codes_map_to_diagnostic_codes(int status, string code) =>
        MacBridgeStatus.ToCode(status).ShouldBe(code);

    [Fact]
    public void Ok_status_does_not_throw() => Should.NotThrow(() => MacBridgeStatus.ThrowIfFailed(MacBridgeStatus.Ok));

    [Fact]
    public void Failed_status_throws_device_key_exception() =>
        Should.Throw<DeviceKeyException>(() => MacBridgeStatus.ThrowIfFailed(-2)).Code.ShouldBe(ManagerDiagnosticCodes.KeyMissing);

    [Fact]
    public void Absent_dylib_is_unavailable_outside_macos()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        MacBridgeLibrary.IsPresent.ShouldBeFalse();
    }

    [Fact(Skip = "macOS with bridge only", SkipUnless = nameof(IsMacWithBridge), SkipType = typeof(MacBridgeKeyProviderTests))]
    [SupportedOSPlatform("macos")]
    public async Task Create_sign_reopen_delete()
    {
        var provider = new MacBridgeKeyProvider();
        var ct = TestContext.Current.CancellationToken;
        var key = await provider.CreateKeyAsync(softwareKeysAllowed: true, ct);
        var data = "payload"u8.ToArray();
        var signature = key.SignEs256(data);
        signature.Length.ShouldBe(64);

        using var ecdsa = ECDsa.Create(key.PublicJwk.ToParameters());
        ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();

        var reopened = await provider.OpenKeyAsync(key.Reference, ct);
        reopened.Thumbprint.ShouldBe(key.Thumbprint);
        await provider.DeleteKeyAsync(key.Reference, ct);
        await Should.ThrowAsync<DeviceKeyException>(() => provider.OpenKeyAsync(key.Reference, ct));
    }
}
