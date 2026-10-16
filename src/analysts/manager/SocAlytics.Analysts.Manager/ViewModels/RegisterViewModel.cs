using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.ViewModels;

public sealed class RegisterViewModel : ObservableBase, IDisposable
{
    public const string Instruction = "Give both to your Registrar in person";

    private readonly IManagerControl _control;
    private readonly Action<Action> _post;
    private string _platformEndpoint = "https://";
    private string? _verificationUri;
    private string? _pairingCode;
    private string? _deviceFingerprint;
    private string _progress = "Enter the platform endpoint.";
    private string? _outcome;
    private bool _busy;

    public RegisterViewModel(IManagerControl control, Action<Action>? post = null)
    {
        _control = control;
        _post = post ?? (a => a());
        _control.StatusChanged += OnStatusChanged;
    }

    public string PlatformEndpoint { get => _platformEndpoint; set => Set(ref _platformEndpoint, value); }

    public string? VerificationUri { get => _verificationUri; private set => Set(ref _verificationUri, value); }

    public string? PairingCode { get => _pairingCode; private set => Set(ref _pairingCode, value); }

    public string? DeviceFingerprint { get => _deviceFingerprint; private set => Set(ref _deviceFingerprint, value); }

    public string Progress { get => _progress; private set => Set(ref _progress, value); }

    public string? Outcome { get => _outcome; private set => Set(ref _outcome, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(CanRegister));
            }
        }
    }

    public bool CanRegister => !Busy && _control.GetStatus().RegistrationState == "unregistered";

    public string InstructionText => Instruction;

    public async Task RegisterAsync()
    {
        if (!CanRegister)
        {
            return;
        }

        Busy = true;
        Outcome = null;
        Progress = "Contacting the platform…";
        try
        {
            var result = await _control.RegisterAsync(new Registration.RegisterRequest(PlatformEndpoint.Trim()));
            if (!result.Succeeded)
            {
                Progress = "Registration did not start.";
                Outcome = $"Refused: {result.Code}{(string.IsNullOrEmpty(result.Notice) ? "" : " - " + result.Notice)}";
                Busy = false;
                return;
            }

            PairingCode = result.PairingCode;
            DeviceFingerprint = result.DeviceFingerprint;
            VerificationUri = result.VerificationUri;
            Progress = "Waiting for your Registrar to approve.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Progress = "Registration failed.";
            Outcome = "Failed: " + e.GetType().Name;
            Busy = false;
        }
    }

    public Task CancelAsync() => _control.CancelRegistrationAsync();

    public void Dispose() => _control.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, EventArgs e) => _post(() =>
    {
        var status = _control.GetStatus();
        Raise(nameof(CanRegister));
        if (!Busy || PairingCode is null)
        {
            return;
        }

        switch (status.RegistrationState)
        {
            case "active":
                Progress = "Registered.";
                Outcome = "Registration is active.";
                Clear();
                break;
            case "unregistered":
                Progress = "Registration ended.";
                Outcome = "Rejected, expired, or cancelled. Nothing was kept.";
                Clear();
                break;
            default:
                Progress = status.PairingPhase == "activating" ? "Activating…" : "Waiting for your Registrar to approve.";
                break;
        }
    });

    private void Clear()
    {
        PairingCode = null;
        Busy = false;
    }
}
