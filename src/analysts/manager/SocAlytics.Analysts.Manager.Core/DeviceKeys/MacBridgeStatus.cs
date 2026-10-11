using System.Reflection;
using System.Runtime.InteropServices;
using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

internal static class MacBridgeStatus
{
    public const int Ok = 0;
    public const int Unavailable = -1;
    public const int KeyMissing = -2;
    public const int Inaccessible = -3;
    public const int BufferTooSmall = -4;
    public const int SoftwareNotAllowed = -5;

    public static void ThrowIfFailed(int status)
    {
        if (status != Ok)
        {
            throw new DeviceKeyException(ToCode(status));
        }
    }

    public static string ToCode(int status) => status switch
    {
        Unavailable => ManagerDiagnosticCodes.NoSupportedKeyStore,
        KeyMissing => ManagerDiagnosticCodes.KeyMissing,
        SoftwareNotAllowed => ManagerDiagnosticCodes.SoftwareKeyNotAllowed,
        _ => ManagerDiagnosticCodes.KeyInaccessible,
    };
}

/// <summary>OS-neutral lookup of the Swift bridge dylib (app directory, then bundle Frameworks).</summary>
internal static class MacBridgeLibrary
{
    public const string Name = "SocAlyticsMacBridge";
    public const string FileName = "libSocAlyticsMacBridge.dylib";

    private static readonly Lazy<nint> Handle = new(Load);

    public static bool IsPresent => Handle.Value != 0;

    public static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == Name ? Handle.Value : 0;

    public static IEnumerable<string> CandidatePaths(string baseDirectory)
    {
        yield return Path.Combine(baseDirectory, FileName);
        yield return Path.GetFullPath(Path.Combine(baseDirectory, "..", "Frameworks", FileName));
    }

    private static nint Load()
    {
        foreach (var path in CandidatePaths(AppContext.BaseDirectory))
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }
}
