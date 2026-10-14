using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Hosting;

public interface IManagerControl
{
    event EventHandler? StatusChanged;

    /// <summary>Raised once a safe exit or session end finished draining and the process should stop.</summary>
    event EventHandler? ExitCompleted;

    /// <summary>Raised when another instance or the CLI asks to bring the status window forward.</summary>
    event EventHandler? ShowStatusRequested;

    void RequestShowStatus();

    ManagerStatusSnapshot GetStatus();

    Task<ControlResult> PauseAsync();

    Task<ControlResult> PauseAsync(TimeSpan duration);

    Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc);

    Task<ControlResult> ResumeAsync();

    Task<ControlResult> ExitAsync();

    Task<ControlResult> SessionEndingAsync();

    Work.ConcurrencySnapshot GetConcurrency();

    Task<ControlResult> SetHostLocalConcurrencyAsync(int limit);

    Task<ControlResult> SetAutostartAsync(bool enabled);

    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task CancelRegistrationAsync();

    Task<ControlResult> UnregisterAsync();

    Task<ControlResult> DiscardLocalRegistrationAsync();
}

public sealed record ControlResult(bool Succeeded, string? Code = null, string? Notice = null);
