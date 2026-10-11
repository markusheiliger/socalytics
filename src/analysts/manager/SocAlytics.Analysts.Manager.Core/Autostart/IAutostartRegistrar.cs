namespace SocAlytics.Analysts.Manager.Autostart;

public interface IAutostartRegistrar
{
    /// <summary>Reads the real OS state; never a cached flag.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    Task EnableAsync(string executablePath, CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);
}

public sealed class AutostartException(string message, Exception? inner = null) : Exception(message, inner);
