using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Hosting;

public interface IManagerControl
{
    event EventHandler? StatusChanged;

    /// <summary>Raised once a safe exit or session end finished draining and the process should stop.</summary>
    event EventHandler? ExitCompleted;

    ManagerStatusSnapshot GetStatus();

    Task<ControlResult> PauseAsync();

    Task<ControlResult> ResumeAsync();

    Task<ControlResult> ExitAsync();

    Task<ControlResult> SessionEndingAsync();

    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task CancelRegistrationAsync();

    Task<ControlResult> UnregisterAsync();

    Task<ControlResult> DiscardLocalRegistrationAsync();
}

public sealed record ControlResult(bool Succeeded, string? Code = null, string? Notice = null);
