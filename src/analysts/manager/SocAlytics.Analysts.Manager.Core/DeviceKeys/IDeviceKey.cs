namespace SocAlytics.Analysts.Manager.DeviceKeys;

public interface IDeviceKey
{
    PublicJwk PublicJwk { get; }

    string Thumbprint { get; }

    KeyProtection Protection { get; }

    DeviceKeyReference Reference { get; }

    /// <summary>Returns the raw 64-byte r‖s ES256 signature over <paramref name="data"/>.</summary>
    byte[] SignEs256(ReadOnlySpan<byte> data);
}
