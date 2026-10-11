using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

[Collection(SoftHsmCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class Pkcs11KeyProviderTests(SoftHsmToken token)
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    private Pkcs11KeyProvider Provider(string? state = null) =>
        Pkcs11KeyProvider.CreateForTests(SoftHsmToken.ModulePath, SoftHsmToken.TokenLabel, state ?? token.NewStateDirectory());

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public async Task Sign_verifies_with_public_point_and_reloads_and_deletes()
    {
        var provider = Provider();
        provider.IsAvailable.ShouldBeTrue();
        var key = await provider.CreateKeyAsync(true, TestContext.Current.CancellationToken);
        key.Protection.ShouldBe(KeyProtection.Software);

        var data = Encoding.ASCII.GetBytes("header.payload");
        var sig = key.SignEs256(data);
        sig.Length.ShouldBe(64);
        using var ecdsa = ECDsa.Create(key.PublicJwk.ToParameters());
        ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();

        var reopened = await provider.OpenKeyAsync(key.Reference, TestContext.Current.CancellationToken);
        reopened.Thumbprint.ShouldBe(key.Thumbprint);

        await provider.DeleteKeyAsync(key.Reference, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<DeviceKeyException>(() => provider.OpenKeyAsync(key.Reference, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe("key-missing");
        Pkcs11LibdlResolver.IsActive.ShouldBeTrue();
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public async Task Software_test_provider_is_refused_when_software_keys_not_allowed()
    {
        var provider = Provider();
        provider.Protection.ShouldBe(KeyProtection.Software);
        Should.Throw<DeviceKeyException>(() => new DeviceKeyProviderSelector([provider]).Select(false))
            .Code.ShouldBe("software-key-not-allowed");
        (await Should.ThrowAsync<DeviceKeyException>(() => provider.CreateKeyAsync(false, TestContext.Current.CancellationToken))).Code.ShouldBe("software-key-not-allowed");
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public void Missing_group_readable_or_wrong_pin_makes_provider_unavailable()
    {
        var empty = Directory.CreateTempSubdirectory("socalytics-state-").FullName;
        Provider(empty).IsAvailable.ShouldBeFalse();
        Provider(token.NewStateDirectory(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead))
            .IsAvailable.ShouldBeFalse();
        Provider(token.NewStateDirectory(pin: "wrong-pin")).IsAvailable.ShouldBeFalse();
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public async Task Unavailable_provider_does_not_create_a_key()
    {
        var provider = Provider(token.NewStateDirectory(pin: "wrong-pin"));
        (await Should.ThrowAsync<DeviceKeyException>(() => provider.CreateKeyAsync(true, TestContext.Current.CancellationToken))).Code.ShouldBe("no-supported-key-store");
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public async Task Generated_private_key_is_sensitive_and_not_extractable()
    {
        var provider = Provider();
        var key = await provider.CreateKeyAsync(true, TestContext.Current.CancellationToken);
        var id = Convert.FromHexString(key.Reference.Reference.Split('|')[2]);
        var factories = new Pkcs11InteropFactories();
        var lib = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, SoftHsmToken.ModulePath, AppType.MultiThreaded);
        var slot = lib.GetSlotList(SlotsType.WithTokenPresent).First(s => s.GetTokenInfo().Label == SoftHsmToken.TokenLabel);
        var session = slot.OpenSession(SessionType.ReadWrite);
        session.Login(CKU.CKU_USER, token.UserPin);
        var h = session.FindAllObjects([
            factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
            factories.ObjectAttributeFactory.Create(CKA.CKA_ID, id)]).Single();
        var attrs = session.GetAttributeValue(h, [CKA.CKA_EXTRACTABLE, CKA.CKA_SENSITIVE]);
        attrs[0].GetValueAsBool().ShouldBeFalse();
        attrs[1].GetValueAsBool().ShouldBeTrue();
        Should.Throw<Exception>(() => session.GetAttributeValue(h, [CKA.CKA_VALUE])[0].GetValueAsByteArray().Length.ShouldBeGreaterThan(0));
        session.Dispose();
        lib.Dispose();
        await provider.DeleteKeyAsync(key.Reference, TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(Pkcs11KeyProviderTests))]
    public void Production_allow_list_refuses_softhsm()
    {
        var allow = new Pkcs11ModuleAllowList("/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so");
        allow.IsAllowed("/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so").ShouldBeTrue();
        allow.IsAllowed(SoftHsmToken.ModulePath).ShouldBeFalse();
        new Pkcs11KeyProvider(SoftHsmToken.ModulePath, SoftHsmToken.TokenLabel, token.NewStateDirectory(), new Pkcs11ModuleAllowList("/x/libtpm2_pkcs11.so"))
            .IsAvailable.ShouldBeFalse();
    }
}
