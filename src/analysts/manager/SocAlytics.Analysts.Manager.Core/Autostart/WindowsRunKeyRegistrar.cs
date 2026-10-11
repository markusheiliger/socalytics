using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SocAlytics.Analysts.Manager.Autostart;

[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyRegistrar : IAutostartRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(AutostartEntryFormats.RunValueName) is not string)
            {
                return Task.FromResult(false);
            }

            // StartupApproved is only read: an odd first byte means "disabled by Windows".
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            var disabled = approved?.GetValue(AutostartEntryFormats.RunValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
            return Task.FromResult(!disabled);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new AutostartException("autostart read failed", e);
        }
    }

    public Task EnableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, true);
            run.SetValue(AutostartEntryFormats.RunValueName, AutostartEntryFormats.RunValue(executablePath));
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new AutostartException("autostart write failed", e);
        }
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            run?.DeleteValue(AutostartEntryFormats.RunValueName, false);
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new AutostartException("autostart remove failed", e);
        }
    }
}
