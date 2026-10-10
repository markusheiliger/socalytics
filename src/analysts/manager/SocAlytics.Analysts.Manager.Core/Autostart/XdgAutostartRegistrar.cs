using System.Runtime.Versioning;

namespace SocAlytics.Analysts.Manager.Autostart;

[SupportedOSPlatform("linux")]
public sealed class XdgAutostartRegistrar : IAutostartRegistrar
{
    private static string FilePath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(config, "autostart", AutostartEntryFormats.DesktopFileName);
        }
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = FilePath;
            return File.Exists(path)
                && AutostartEntryFormats.DesktopEntryEnabled(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart read failed", e);
        }
    }

    public async Task EnableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, AutostartEntryFormats.DesktopEntry(executablePath), cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart write failed", e);
        }
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            File.Delete(FilePath);
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AutostartException("autostart remove failed", e);
        }
    }
}
