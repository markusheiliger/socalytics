using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed class InMemoryDeviceKeyProvider(KeyProtection protection = KeyProtection.Software, bool isAvailable = true)
    : IDeviceKeyProvider
{
    private readonly Dictionary<string, InMemoryKey> _keys = [];

    public string Id => "in-memory";

    public bool IsAvailable => isAvailable;

    public KeyProtection Protection => protection;

    public int CreatedCount { get; private set; }

    public int LiveCount => _keys.Count;

    public static string FixtureDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "registration", "v1");

    public IDeviceKey ImportFixtureKey()
    {
        var json = File.ReadAllText(Path.Combine(FixtureDirectory, "test-key.jwk.json"));
        var parameters = PublicJwk.FromJson(json).ToParameters();
        parameters.D = PublicJwk.FromBase64Url((string)JsonNode.Parse(json)!["d"]!);
        return Add(ECDsa.Create(parameters));
    }

    public Task<IDeviceKey> CreateKeyAsync(bool softwareKeysAllowed, CancellationToken cancellationToken = default)
    {
        CreatedCount++;
        return Task.FromResult<IDeviceKey>(Add(ECDsa.Create(ECCurve.NamedCurves.nistP256)));
    }

    public Task<IDeviceKey> OpenKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default) =>
        _keys.TryGetValue(reference.Reference, out var key)
            ? Task.FromResult<IDeviceKey>(key)
            : throw new DeviceKeyException("key-missing");

    public Task DeleteKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        _keys.Remove(reference.Reference);
        return Task.CompletedTask;
    }

    private InMemoryKey Add(ECDsa ecdsa)
    {
        var key = new InMemoryKey(ecdsa, protection, new DeviceKeyReference(DeviceKeyReference.Pkcs11, Guid.NewGuid().ToString("N")));
        _keys[key.Reference.Reference] = key;
        return key;
    }

    private sealed class InMemoryKey : IDeviceKey
    {
        private readonly ECDsa _ecdsa;

        public InMemoryKey(ECDsa ecdsa, KeyProtection protection, DeviceKeyReference reference)
        {
            _ecdsa = ecdsa;
            Protection = protection;
            Reference = reference;
            PublicJwk = PublicJwk.FromParameters(ecdsa.ExportParameters(false));
            Thumbprint = JwkThumbprint.Compute(PublicJwk);
        }

        public PublicJwk PublicJwk { get; }

        public string Thumbprint { get; }

        public KeyProtection Protection { get; }

        public DeviceKeyReference Reference { get; }

        public byte[] SignEs256(ReadOnlySpan<byte> data) =>
            _ecdsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
