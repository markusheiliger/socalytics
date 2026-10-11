using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

[SupportedOSPlatform("macos")]
internal static class MacBridgeNative
{
    private const string Lib = MacBridgeLibrary.Name;

    static MacBridgeNative() =>
        NativeLibrary.SetDllImportResolver(typeof(MacBridgeNative).Assembly, MacBridgeLibrary.Resolve);

    [DllImport(Lib, EntryPoint = "socalytics_se_available")]
    public static extern int SeAvailable();

    [DllImport(Lib, EntryPoint = "socalytics_key_create")]
    public static extern int KeyCreate(int allowSoftware, byte[] reference, int referenceCapacity, out int referenceLength, byte[] x, byte[] y, out int protection);

    [DllImport(Lib, EntryPoint = "socalytics_key_open")]
    public static extern int KeyOpen(byte[] reference, int referenceLength, byte[] x, byte[] y, out int protection);

    [DllImport(Lib, EntryPoint = "socalytics_key_sign")]
    public static extern int KeySign(byte[] reference, int referenceLength, byte[] data, int dataLength, byte[] signature, int signatureCapacity, out int signatureLength);

    [DllImport(Lib, EntryPoint = "socalytics_key_public")]
    public static extern int KeyPublic(byte[] reference, int referenceLength, byte[] x, byte[] y);

    [DllImport(Lib, EntryPoint = "socalytics_key_delete")]
    public static extern int KeyDelete(byte[] reference, int referenceLength);

    [DllImport(Lib, EntryPoint = "socalytics_autostart_register")]
    public static extern int AutostartRegister();

    [DllImport(Lib, EntryPoint = "socalytics_autostart_unregister")]
    public static extern int AutostartUnregister();

    [DllImport(Lib, EntryPoint = "socalytics_autostart_status")]
    public static extern int AutostartStatus();

    [DllImport(Lib, EntryPoint = "socalytics_free")]
    public static extern void Free(nint pointer);
}
