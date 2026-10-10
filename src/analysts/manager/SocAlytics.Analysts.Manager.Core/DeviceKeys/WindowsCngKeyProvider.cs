using System.Runtime.Versioning;
using System.Security.Cryptography;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCngKeyProvider : IDeviceKeyProvider
{
    private const int NteBadKeyset = unchecked((int)0x80090016);

    private static readonly CngProvider PlatformProvider = new(WindowsCngKeyReference.PlatformProviderName);

    private readonly Lazy<bool> _tpmAvailable = new(ProbeTpm);

    public string Id => DeviceKeyReference.WindowsCng;

    public bool IsAvailable => true;

    public KeyProtection Protection => _tpmAvailable.Value ? KeyProtection.Tpm : KeyProtection.Software;

    public Task<IDeviceKey> CreateKeyAsync(bool softwareKeysAllowed, CancellationToken cancellationToken = default)
    {
        var tpm = _tpmAvailable.Value;
        if (!tpm && !softwareKeysAllowed)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
        }

        var provider = tpm ? PlatformProvider : CngProvider.MicrosoftSoftwareKeyStorageProvider;
        var providerName = tpm ? WindowsCngKeyReference.PlatformProviderName : WindowsCngKeyReference.SoftwareProviderName;
        var name = WindowsCngKeyReference.NewKeyName();
        try
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = CngKeyCreationOptions.None,
            };
            var cng = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
            return Task.FromResult<IDeviceKey>(Wrap(cng, providerName, name, tpm ? KeyProtection.Tpm : KeyProtection.Software));
        }
        catch (CryptographicException)
        {
            throw new DeviceKeyException(tpm ? ManagerDiagnosticCodes.KeyInaccessible : ManagerDiagnosticCodes.NoSupportedKeyStore);
        }
    }

    public Task<IDeviceKey> OpenKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var (provider, providerName, name) = Parse(reference);
        try
        {
            var cng = CngKey.Open(name, provider);
            var protection = providerName == WindowsCngKeyReference.PlatformProviderName ? KeyProtection.Tpm : KeyProtection.Software;
            return Task.FromResult<IDeviceKey>(Wrap(cng, providerName, name, protection));
        }
        catch (CryptographicException ex)
        {
            throw Map(ex);
        }
    }

    public Task DeleteKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var (provider, _, name) = Parse(reference);
        try
        {
            using var cng = CngKey.Open(name, provider);
            cng.Delete();
        }
        catch (CryptographicException ex)
        {
            throw Map(ex);
        }

        return Task.CompletedTask;
    }

    private static bool ProbeTpm()
    {
        try
        {
            _ = CngKey.Exists(WindowsCngKeyReference.NewKeyName(), PlatformProvider);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static (CngProvider Provider, string ProviderName, string KeyName) Parse(DeviceKeyReference reference)
    {
        if (!WindowsCngKeyReference.TryParse(reference, out var providerName, out var keyName))
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        }

        return (new CngProvider(providerName), providerName, keyName);
    }

    private static DeviceKeyException Map(CryptographicException ex) =>
        new(ex.HResult == NteBadKeyset ? ManagerDiagnosticCodes.KeyMissing : ManagerDiagnosticCodes.KeyInaccessible);

    private static WindowsCngKey Wrap(CngKey cng, string providerName, string name, KeyProtection protection)
    {
        using var ecdsa = new ECDsaCng(cng);
        var q = ecdsa.ExportParameters(false).Q;
        var jwk = new PublicJwk(PublicJwk.Base64Url(q.X!), PublicJwk.Base64Url(q.Y!));
        return new WindowsCngKey(cng, jwk, WindowsCngKeyReference.Format(providerName, name), protection);
    }

    private sealed class WindowsCngKey(CngKey cng, PublicJwk jwk, DeviceKeyReference reference, KeyProtection protection) : IDeviceKey
    {
        public PublicJwk PublicJwk => jwk;

        public string Thumbprint { get; } = JwkThumbprint.Compute(jwk);

        public KeyProtection Protection => protection;

        public DeviceKeyReference Reference => reference;

        public byte[] SignEs256(ReadOnlySpan<byte> data)
        {
            try
            {
                using var ecdsa = new ECDsaCng(cng);
                return ecdsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException ex)
            {
                throw Map(ex);
            }
        }
    }
}
