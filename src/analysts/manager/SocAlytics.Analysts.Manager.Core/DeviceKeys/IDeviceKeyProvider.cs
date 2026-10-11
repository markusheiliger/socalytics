namespace SocAlytics.Analysts.Manager.DeviceKeys;

public interface IDeviceKeyProvider
{
    string Id { get; }

    bool IsAvailable { get; }

    KeyProtection Protection { get; }

    Task<IDeviceKey> CreateKeyAsync(bool softwareKeysAllowed, CancellationToken cancellationToken = default);

    Task<IDeviceKey> OpenKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default);

    Task DeleteKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default);
}
