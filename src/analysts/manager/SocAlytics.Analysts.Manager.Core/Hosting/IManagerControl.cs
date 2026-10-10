using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Hosting;

public interface IManagerControl
{
    event EventHandler? StatusChanged;

    ManagerStatusSnapshot GetStatus();

    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task CancelRegistrationAsync();

    Task<ControlResult> UnregisterAsync();

    Task<ControlResult> DiscardLocalRegistrationAsync();
}

public sealed record ControlResult(bool Succeeded, string? Code = null, string? Notice = null);
