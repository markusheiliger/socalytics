using System.Runtime.Versioning;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Autostart;

[SupportedOSPlatform("macos")]
public sealed class MacAutostartRegistrar : IAutostartRegistrar
{
    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents",
        AutostartEntryFormats.MacBundleId + ".plist");

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (TryBridge(MacBridgeNative.AutostartStatus) is { } status && status >= 0)
        {
            return Task.FromResult(status == 1);
        }

        return Task.FromResult(File.Exists(PlistPath));
    }

    public async Task EnableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (TryBridge(MacBridgeNative.AutostartRegister) == MacBridgeStatus.Ok)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            await File.WriteAllTextAsync(PlistPath, AutostartEntryFormats.LaunchAgentPlist(executablePath), cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart write failed", e);
        }
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        TryBridge(MacBridgeNative.AutostartUnregister);
        try
        {
            File.Delete(PlistPath);
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart remove failed", e);
        }
    }

    private static int? TryBridge(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}
