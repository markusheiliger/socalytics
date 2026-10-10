using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal sealed class FakeManagerControl : IManagerControl
{
    public string State { get; set; } = "unregistered";

    public string? Fingerprint { get; set; }

    public event EventHandler? StatusChanged;

    public event EventHandler? ExitCompleted;

    public Task<ControlResult> PauseAsync() => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> PauseAsync(TimeSpan duration) => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> PauseUntilAsync(DateTimeOffset endUtc) => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> ResumeAsync() => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> ExitAsync() => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> SessionEndingAsync() => Task.FromResult(new ControlResult(true));

    public void Raise() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseExit() => ExitCompleted?.Invoke(this, EventArgs.Empty);

    public ManagerStatusSnapshot GetStatus() => new(
        State, null, null, "stamp-1", "https://platform.example", null, new ConnectivityStatus("unknown"), null, null,
        false, null, 0, null, [], null, null, "tpm", null, "none", Fingerprint);

    public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RegisterResult(true, null, "ABCD-1234", "fp-xyz", "https://platform.example/pair"));

    public Task CancelRegistrationAsync() => Task.CompletedTask;

    public Task<ControlResult> UnregisterAsync() => Task.FromResult(new ControlResult(true));

    public Task<ControlResult> DiscardLocalRegistrationAsync() => Task.FromResult(new ControlResult(true));
}
