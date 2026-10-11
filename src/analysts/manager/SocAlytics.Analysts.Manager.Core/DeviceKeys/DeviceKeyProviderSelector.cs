using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public sealed class DeviceKeyProviderSelector(IEnumerable<IDeviceKeyProvider> providers)
{
    private readonly IReadOnlyList<IDeviceKeyProvider> _providers = providers.ToList();

    /// <summary>Selects a provider without creating any key; refuses with a <see cref="DeviceKeyException"/>.</summary>
    public IDeviceKeyProvider Select(bool softwareKeysAllowed)
    {
        var available = _providers.Where(p => p.IsAvailable).ToList();
        var hardware = available.FirstOrDefault(p => p.Protection != KeyProtection.Software);
        if (hardware is not null)
        {
            return hardware;
        }

        var software = available.FirstOrDefault();
        if (software is null)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.NoSupportedKeyStore);
        }

        return softwareKeysAllowed
            ? software
            : throw new DeviceKeyException(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
    }
}
