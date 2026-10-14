using System.Runtime.Versioning;
using System.Security.Cryptography;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

[SupportedOSPlatform("linux")]
internal sealed class Pkcs11KeyProvider : IDeviceKeyProvider
{
    public const string PinFileName = "pkcs11.pin";

    private static readonly byte[] P256Params = [0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07];

    private readonly string _modulePath;
    private readonly string _tokenLabel;
    private readonly string _pinFile;
    private readonly KeyProtection _protection;
    private readonly bool _moduleAllowed;

    public Pkcs11KeyProvider(string modulePath, string tokenLabel, string stateDirectory, Pkcs11ModuleAllowList allowList)
        : this(modulePath, tokenLabel, stateDirectory, allowList.IsAllowed(modulePath), KeyProtection.Pkcs11Token)
    {
    }

    private Pkcs11KeyProvider(string modulePath, string tokenLabel, string stateDirectory, bool allowed, KeyProtection protection)
    {
        _modulePath = modulePath;
        _tokenLabel = tokenLabel;
        _pinFile = Path.Combine(stateDirectory, PinFileName);
        _moduleAllowed = allowed;
        _protection = protection;
    }

    /// <summary>Test-only: accepts any module path and reports protection <c>software</c>.</summary>
    internal static Pkcs11KeyProvider CreateForTests(string modulePath, string tokenLabel, string stateDirectory) =>
        new(modulePath, tokenLabel, stateDirectory, true, KeyProtection.Software);

    public string Id => DeviceKeyReference.Pkcs11;

    public KeyProtection Protection => _protection;

    public bool IsAvailable
    {
        get
        {
            if (!_moduleAllowed || !File.Exists(_modulePath) || ReadPin() is null)
            {
                return false;
            }

            try
            {
                using var ctx = Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task<IDeviceKey> CreateKeyAsync(bool softwareKeysAllowed, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.NoSupportedKeyStore);
        }

        if (_protection == KeyProtection.Software && !softwareKeysAllowed)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
        }

        using var ctx = OpenOrInaccessible();
        var f = ctx.Factories;
        var id = RandomNumberGenerator.GetBytes(16);
        var pub = new List<IObjectAttribute>
        {
            f.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_VERIFY, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_ID, id),
            f.ObjectAttributeFactory.Create(CKA.CKA_EC_PARAMS, P256Params),
        };
        var priv = new List<IObjectAttribute>
        {
            f.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_SIGN, true),
            f.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, false),
            f.ObjectAttributeFactory.Create(CKA.CKA_ID, id),
        };
        using var mechanism = f.MechanismFactory.Create(CKM.CKM_EC_KEY_PAIR_GEN);
        ctx.Session.GenerateKeyPair(mechanism, pub, priv, out var pubHandle, out _);
        var jwk = ReadPublic(ctx.Session, pubHandle);
        return Task.FromResult<IDeviceKey>(new Pkcs11Key(this, jwk, MakeReference(id)));
    }

    public Task<IDeviceKey> OpenKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var id = ParseReference(reference);
        using var ctx = OpenOrInaccessible();
        var pub = Find(ctx, CKO.CKO_PUBLIC_KEY, id) ?? throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        return Task.FromResult<IDeviceKey>(new Pkcs11Key(this, ReadPublic(ctx.Session, pub), reference));
    }

    public Task DeleteKeyAsync(DeviceKeyReference reference, CancellationToken cancellationToken = default)
    {
        var id = ParseReference(reference);
        using var ctx = OpenOrInaccessible();
        foreach (var cls in new[] { CKO.CKO_PRIVATE_KEY, CKO.CKO_PUBLIC_KEY })
        {
            if (Find(ctx, cls, id) is { } handle)
            {
                ctx.Session.DestroyObject(handle);
            }
        }

        return Task.CompletedTask;
    }

    internal byte[] Sign(DeviceKeyReference reference, byte[] data)
    {
        var id = ParseReference(reference);
        using var ctx = OpenOrInaccessible();
        var priv = Find(ctx, CKO.CKO_PRIVATE_KEY, id) ?? throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        using var mechanism = ctx.Factories.MechanismFactory.Create(CKM.CKM_ECDSA);
        return ctx.Session.Sign(mechanism, priv, SHA256.HashData(data));
    }

    private DeviceKeyReference MakeReference(byte[] id) =>
        new(Id, $"{_modulePath}|{_tokenLabel}|{Convert.ToHexString(id)}");

    private static byte[] ParseReference(DeviceKeyReference reference)
    {
        var parts = reference.Reference.Split('|');
        if (reference.Provider != DeviceKeyReference.Pkcs11 || parts.Length != 3)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        }

        try
        {
            return Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyMissing);
        }
    }

    private static IObjectHandle? Find(Context ctx, CKO cls, byte[] id)
    {
        var f = ctx.Factories;
        var found = ctx.Session.FindAllObjects(
        [
            f.ObjectAttributeFactory.Create(CKA.CKA_CLASS, cls),
            f.ObjectAttributeFactory.Create(CKA.CKA_ID, id),
        ]);
        return found.Count > 0 ? found[0] : null;
    }

    private static PublicJwk ReadPublic(ISession session, IObjectHandle handle)
    {
        var attrs = session.GetAttributeValue(handle, [CKA.CKA_EC_POINT]);
        var point = attrs[0].GetValueAsByteArray();
        // CKA_EC_POINT is a DER OCTET STRING wrapping the uncompressed point 04‖X‖Y.
        if (point.Length == 67 && point[0] == 0x04 && point[1] == 0x41)
        {
            point = point[2..];
        }

        if (point.Length != 65 || point[0] != 0x04)
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyInaccessible);
        }

        return new PublicJwk(PublicJwk.Base64Url(point.AsSpan(1, 32)), PublicJwk.Base64Url(point.AsSpan(33, 32)));
    }

    private byte[]? ReadPin()
    {
        try
        {
            if (!File.Exists(_pinFile)
                || (File.GetUnixFileMode(_pinFile) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                return null;
            }

            var pin = File.ReadAllText(_pinFile).Trim();
            return pin.Length == 0 ? null : System.Text.Encoding.UTF8.GetBytes(pin);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Context OpenOrInaccessible()
    {
        try
        {
            return Open();
        }
        catch (DeviceKeyException)
        {
            throw;
        }
        catch
        {
            throw new DeviceKeyException(ManagerDiagnosticCodes.KeyInaccessible);
        }
    }

    private Context Open()
    {
        var pin = ReadPin() ?? throw new DeviceKeyException(ManagerDiagnosticCodes.KeyInaccessible);
        Pkcs11LibdlResolver.EnsureRegistered();
        // Module initialization and finalization are process-global, so contexts never overlap.
        ModuleGate.Wait();
        IPkcs11Library? library = null;
        try
        {
            var factories = new Pkcs11InteropFactories();
            library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, _modulePath, AppType.MultiThreaded);
            var slot = library.GetSlotList(SlotsType.WithTokenPresent)
                .FirstOrDefault(s => s.GetTokenInfo().Label == _tokenLabel)
                ?? throw new DeviceKeyException(ManagerDiagnosticCodes.KeyInaccessible);
            var session = slot.OpenSession(SessionType.ReadWrite);
            try
            {
                session.Login(CKU.CKU_USER, pin);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            return new Context(factories, library, session);
        }
        catch
        {
            try
            {
                library?.Dispose();
            }
            finally
            {
                ModuleGate.Release();
            }

            throw;
        }
    }

    private static readonly SemaphoreSlim ModuleGate = new(1, 1);

    private sealed class Context(Pkcs11InteropFactories factories, IPkcs11Library library, ISession session) : IDisposable
    {
        public Pkcs11InteropFactories Factories => factories;

        public ISession Session => session;

        public void Dispose()
        {
            try
            {
                session.Logout();
            }
            catch (Exception)
            {
                // Best effort; the session is closed below.
            }

            try
            {
                session.Dispose();
                library.Dispose();
            }
            finally
            {
                ModuleGate.Release();
            }
        }
    }

    private sealed class Pkcs11Key(Pkcs11KeyProvider provider, PublicJwk jwk, DeviceKeyReference reference) : IDeviceKey
    {
        public PublicJwk PublicJwk => jwk;

        public string Thumbprint { get; } = JwkThumbprint.Compute(jwk);

        public KeyProtection Protection => provider._protection;

        public DeviceKeyReference Reference => reference;

        public byte[] SignEs256(ReadOnlySpan<byte> data) => provider.Sign(reference, data.ToArray());
    }
}
