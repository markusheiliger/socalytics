using System.Reflection;
using System.Runtime.InteropServices;
using Net.Pkcs11Interop.HighLevelAPI;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

/// <summary>Maps the <c>libdl</c> import of Pkcs11Interop to <c>libdl.so.2</c>, which exists on current glibc systems.</summary>
internal static class Pkcs11LibdlResolver
{
    private static readonly Lazy<bool> Registration = new(Register);

    public static bool IsActive => Registration.Value;

    public static void EnsureRegistered() => _ = Registration.Value;

    private static bool Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(Pkcs11InteropFactories).Assembly, Resolve);
        return true;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == "libdl" && NativeLibrary.TryLoad("libdl.so.2", out var handle) ? handle : IntPtr.Zero;
}
