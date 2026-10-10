using System.Runtime.Versioning;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

[SupportedOSPlatform("macos")]
internal sealed class MacBridgeKeyProvider : IDeviceKeyProvider
{
    private const int ReferenceCapacity = 2048;

    public string Id => DeviceKeyReference.MacosBridge;

    public bool IsAvailable => MacBridgeLibrary.IsPresent;

    public KeyProtection Protection =>
        IsAvailable && MacBridgeNative.SeAvailable() == 1 ? KeyProtection.SecureEnclave : KeyProtection.Software;

    public Task<IDeviceKey> CreateKeyAsync(bool softwareKeysAllowed, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.NoSupportedKeyStore);
        }

        var reference = new byte[ReferenceCapacity];
        var x = new byte[32];
        var y = new byte[32];
        MacBridgeStatus.ThrowIfFailed(MacBridgeNative.KeyCreate(
            softwareKeysAllowed ? 1 : 0, reference, reference.Length, out var length, x, y, out var protection));
        var bytes = reference.AsSpan(0, length).ToArray();
        return Task.FromResult<IDeviceKey>(new MacBridgeKey(bytes, x, y, ToProtection(protection)));
    }

    public Task<IDeviceKey> OpenKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var bytes = Decode(reference);
        var x = new byte[32];
        var y = new byte[32];
        MacBridgeStatus.ThrowIfFailed(MacBridgeNative.KeyOpen(bytes, bytes.Length, x, y, out var protection));
        return Task.FromResult<IDeviceKey>(new MacBridgeKey(bytes, x, y, ToProtection(protection)));
    }

    public Task DeleteKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var bytes = Decode(reference);
        MacBridgeStatus.ThrowIfFailed(MacBridgeNative.KeyDelete(bytes, bytes.Length));
        return Task.CompletedTask;
    }

    private static KeyProtection ToProtection(int value) => value == 1 ? KeyProtection.SecureEnclave : KeyProtection.Software;

    private static byte[] Decode(DeviceKeyReference reference)
    {
        if (!MacBridgeLibrary.IsPresent)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.NoSupportedKeyStore);
        }

        try
        {
            return reference.Provider == DeviceKeyReference.MacosBridge
                ? PublicJwk.FromBase64Url(reference.Reference)
                : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        }
    }

    private sealed class MacBridgeKey(byte[] reference, byte[] x, byte[] y, KeyProtection protection) : IDeviceKey
    {
        public PublicJwk PublicJwk { get; } = new(PublicJwk.Base64Url(x), PublicJwk.Base64Url(y));

        public string Thumbprint => JwkThumbprint.Compute(PublicJwk);

        public KeyProtection Protection => protection;

        public DeviceKeyReference Reference { get; } =
            new(DeviceKeyReference.MacosBridge, DeviceKeys.PublicJwk.Base64Url(reference));

        public byte[] SignEs256(ReadOnlySpan<byte> data)
        {
            var signature = new byte[128];
            MacBridgeStatus.ThrowIfFailed(MacBridgeNative.KeySign(
                reference, reference.Length, data.ToArray(), data.Length, signature, signature.Length, out var length));
            var result = signature.AsSpan(0, length);
            return protection == KeyProtection.SecureEnclave && length == EcdsaSignatureFormat.RawLength
                ? result.ToArray()
                : EcdsaSignatureFormat.DerToRaw(result);
        }
    }
}
