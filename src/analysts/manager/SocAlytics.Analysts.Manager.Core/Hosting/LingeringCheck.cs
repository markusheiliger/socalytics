using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SocAlytics.Analysts.Manager.Hosting;

public enum LingeringState
{
    On,
    Off,
    Unknown,
}

/// <summary>Reads systemd's lingering marker (<c>/var/lib/systemd/linger/&lt;user&gt;</c>); needs no D-Bus session.</summary>
public sealed class LingeringCheck(string? root = null, string? userName = null)
{
    public const string DefaultRoot = "/var/lib/systemd/linger";

    public string UserName { get; } = userName ?? Environment.UserName;

    public LingeringState Evaluate()
    {
        var directory = root ?? DefaultRoot;
        try
        {
            if (!Directory.Exists(directory))
            {
                return LingeringState.Off;
            }

            // Listing the directory proves it is readable; a missing marker then means lingering is off.
            _ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();
            return File.Exists(Path.Combine(directory, UserName)) ? LingeringState.On : LingeringState.Off;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LingeringState.Unknown;
        }
    }

    public static string ToWireValue(LingeringState state) => state switch
    {
        LingeringState.On => "on",
        LingeringState.Off => "off",
        _ => "unknown",
    };

    public static string Hint(string userName) =>
        $"Run 'sudo loginctl enable-linger {userName}' so the Manager keeps running without a session.";

    public string Warning() =>
        $"Lingering is off for account '{UserName}': the Manager stops with the last session. {Hint(UserName)}";
}

/// <summary>Warns at start in the headless profile when lingering is off; never stops the Manager.</summary>
public sealed class LingeringWarningService(
    ApplicationProfile profile, LingeringCheck check, ILogger<LingeringWarningService> logger, TextWriter error) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (profile == ApplicationProfile.HeadlessServer && check.Evaluate() == LingeringState.Off)
        {
            var warning = check.Warning();
            logger.LogWarning("{Warning}", warning);
            try
            {
                error.WriteLine(warning);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
