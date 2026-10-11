using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

public class DeviceKeyProviderSelectorTests
{
    [Fact]
    public void Hardware_is_preferred_over_software()
    {
        var software = new InMemoryDeviceKeyProvider();
        var tpm = new InMemoryDeviceKeyProvider(KeyProtection.Tpm);

        new DeviceKeyProviderSelector([software, tpm]).Select(softwareKeysAllowed: true).ShouldBeSameAs(tpm);
    }

    [Fact]
    public void Hardware_is_used_when_software_is_not_allowed()
    {
        var tpm = new InMemoryDeviceKeyProvider(KeyProtection.Tpm);

        new DeviceKeyProviderSelector([tpm]).Select(false).ShouldBeSameAs(tpm);
    }

    [Fact]
    public void Software_is_used_only_when_allowed()
    {
        var software = new InMemoryDeviceKeyProvider();

        new DeviceKeyProviderSelector([software]).Select(true).ShouldBeSameAs(software);
    }

    [Fact]
    public void Software_only_refuses_without_creating_a_key()
    {
        var software = new InMemoryDeviceKeyProvider();

        var ex = Should.Throw<DeviceKeyException>(() => new DeviceKeyProviderSelector([software]).Select(false));

        ex.Code.ShouldBe("software-key-not-allowed");
        software.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public void Nothing_available_refuses()
    {
        var unavailable = new InMemoryDeviceKeyProvider(KeyProtection.Tpm, isAvailable: false);

        Should.Throw<DeviceKeyException>(() => new DeviceKeyProviderSelector([unavailable]).Select(true))
            .Code.ShouldBe("no-supported-key-store");
        Should.Throw<DeviceKeyException>(() => new DeviceKeyProviderSelector([]).Select(true))
            .Code.ShouldBe("no-supported-key-store");
        unavailable.CreatedCount.ShouldBe(0);
    }
}
