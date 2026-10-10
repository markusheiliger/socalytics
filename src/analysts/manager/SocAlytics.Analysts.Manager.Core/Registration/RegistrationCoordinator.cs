using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Registration;

public sealed record RegisterRequest(string PlatformEndpoint, string? StampId = null, string? HostLabel = null);

public sealed record RegisterResult(
    bool Succeeded, string? Code = null, string? PairingCode = null, string? DeviceFingerprint = null,
    string? VerificationUri = null, DateTimeOffset? ExpiresAt = null);

public sealed record ActiveRegistration(
    string ManagerId, string StampId, Uri PlatformEndpoint, DeviceKeyReference KeyReference, KeyProtection Protection,
    string DeviceFingerprint);

public sealed class RegistrationCoordinator(
    PlatformClient platform,
    DeviceKeyProviderSelector selector,
    TimeProvider time,
    ILogger<RegistrationCoordinator> logger)
{
    private const string ManagerVersion = "0.1.0";
    private readonly object _gate = new();
    private ManagerRegistrationState _state = ManagerRegistrationState.Unregistered;
    private PairingPhase? _phase;
    private Uri? _endpoint;
    private string? _stampId;
    private string? _fingerprint;
    private KeyProtection? _protection;
    private ActiveRegistration? _active;
    private CancellationTokenSource? _cts;

    public event EventHandler? Changed;

    public ManagerRegistrationState State { get { lock (_gate) { return _state; } } }

    public PairingPhase? Phase { get { lock (_gate) { return _phase; } } }

    public Uri? PlatformEndpoint { get { lock (_gate) { return _endpoint; } } }

    public string? StampId { get { lock (_gate) { return _stampId; } } }

    public string? Fingerprint { get { lock (_gate) { return _fingerprint; } } }

    public KeyProtection? Protection { get { lock (_gate) { return _protection; } } }

    public ActiveRegistration? Active { get { lock (_gate) { return _active; } } }

    /// <summary>Completes when the current pairing ended in <c>active</c> or <c>unregistered</c>.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.PlatformEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return Refuse(ManagerDiagnosticCodes.InsecureEndpoint);
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_state != ManagerRegistrationState.Unregistered)
            {
                return Refuse("registration-not-allowed");
            }

            _state = ManagerRegistrationState.Pairing;
            _phase = PairingPhase.AwaitingSubmission;
            _endpoint = endpoint;
            cts = _cts = new CancellationTokenSource();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            var started = await StartAsync(request, endpoint, linked.Token);
            var issued = started.Issued;
            lock (_gate)
            {
                _fingerprint = issued.DeviceFingerprint;
                _protection = started.Session.Key.Protection;
                _stampId = issued.StampId;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            var running = started.Session;
            Completion = Task.Run(() => PollAsync(running, cts.Token));
            return new RegisterResult(
                true, null, running.PairingCode, issued.DeviceFingerprint, running.VerificationUri, running.ExpiresAt);
        }
        catch (RegistrationRefusedException e)
        {
            await DiscardAsync(e.Session);
            Reset();
            return Refuse(e.Code);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or PlatformException)
        {
            Reset();
            return Refuse(e is PlatformException { Code: var code } ? code : ManagerDiagnosticCodes.PlatformUnreachable);
        }
    }

    public async Task CancelAsync()
    {
        CancellationTokenSource? cts;
        Task completion;
        lock (_gate)
        {
            if (_state != ManagerRegistrationState.Pairing)
            {
                return;
            }

            cts = _cts;
            completion = Completion;
        }

        cts?.Cancel();
        await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed record Started(PairingSession Session, PairingIssued Issued);

    private sealed class RegistrationRefusedException(string code, PairingSession? session) : Exception(code)
    {
        public string Code { get; } = code;

        public PairingSession? Session { get; } = session;
    }

    private async Task<Started> StartAsync(RegisterRequest request, Uri endpoint, CancellationToken ct)
    {
        var discovery = await platform.SendAsync(platform.BuildDiscovery(endpoint), ct);
        var stamp = discovery.Status == HttpStatusCode.OK ? discovery.Read<StampDiscovery>() : null;
        if (stamp is null)
        {
            throw new RegistrationRefusedException(ManagerDiagnosticCodes.PlatformUnreachable, null);
        }

        if (request.StampId is not null && request.StampId != stamp.StampId)
        {
            throw new RegistrationRefusedException(ManagerDiagnosticCodes.StampMismatch, null);
        }

        IDeviceKeyProvider provider;
        try
        {
            provider = selector.Select(stamp.SoftwareKeysAllowed);
        }
        catch (DeviceKeyException e)
        {
            throw new RegistrationRefusedException(e.Code, null);
        }

        var key = await provider.CreateKeyAsync(stamp.SoftwareKeysAllowed, ct);
        var holder = new PairingSession(endpoint, provider, key, string.Empty, string.Empty, string.Empty, default, default);
        try
        {
            var metadata = new DeviceMetadata(
                OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                key.Protection.ToWireValue(),
                Truncate(Environment.OSVersion.VersionString, 64),
                RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64",
                ManagerVersion,
                request.HostLabel);
            var response = await platform.SendAsync(platform.BuildPairing(endpoint, stamp.StampId, key, metadata), ct);
            var issued = response.Status == HttpStatusCode.Created ? response.Read<PairingIssued>() : null;
            if (issued is null || issued.DeviceFingerprint != DeviceFingerprint.Compute(key.PublicJwk))
            {
                throw new RegistrationRefusedException(ManagerDiagnosticCodes.PairingRefused, holder);
            }

            var interval = TimeSpan.FromSeconds(Math.Max(issued.PollingIntervalSeconds, 1));
            var session = new PairingSession(
                endpoint, provider, key, issued.PollingHandle, issued.VerificationUri, issued.PairingCode,
                issued.ExpiresAt, interval);
            return new Started(session, issued);
        }
        catch (Exception e) when (e is not RegistrationRefusedException)
        {
            await DiscardAsync(holder);
            throw;
        }
    }

    private async Task PollAsync(PairingSession session, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(session.PollingInterval, time, ct);
                if (time.GetUtcNow() >= session.ExpiresAt)
                {
                    break;
                }

                try
                {
                    if (await StepAsync(session, ct))
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    logger.LogDebug("Pairing poll failed: {Code}", ManagerDiagnosticCodes.PlatformUnreachable);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await DiscardAsync(session);
        Reset();
    }

    // Returns true when polling is finished: active, or the registration ended and cleanup is needed by the caller.
    private async Task<bool> StepAsync(PairingSession session, CancellationToken ct)
    {
        var response = await platform.SendAsync(platform.BuildPairingStatus(session.Endpoint, session.PollingHandle), ct);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new OperationCanceledException();
        }

        var status = response.Read<PairingStatus>() ?? throw new OperationCanceledException();
        switch (status.Status)
        {
            case "awaiting_submission":
                SetPhase(PairingPhase.AwaitingSubmission);
                return false;
            case "pending_approval":
                SetPhase(PairingPhase.PendingApproval);
                return false;
            case "approved" when status.Challenge is not null:
                SetPhase(PairingPhase.Activating);
                session.Challenge = status.Challenge;
                return await ActivateAsync(session, ct);
            default:
                throw new OperationCanceledException();
        }
    }

    private async Task<bool> ActivateAsync(PairingSession session, CancellationToken ct)
    {
        var request = platform.BuildActivation(session.Endpoint, session.Key, session.PollingHandle, session.Challenge!);
        var response = await platform.SendAsync(request, ct);
        session.Challenge = null;
        if (response.Status == HttpStatusCode.BadRequest)
        {
            return false;
        }

        var result = response.Status == HttpStatusCode.OK ? response.Read<ActivationResult>() : null;
        if (result is null)
        {
            throw new OperationCanceledException();
        }

        lock (_gate)
        {
            _active = new ActiveRegistration(
                result.ManagerId, result.StampId, session.Endpoint, session.Key.Reference, session.Key.Protection,
                _fingerprint ?? string.Empty);
            _stampId = result.StampId;
            _state = ManagerRegistrationState.Active;
            _phase = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SetPhase(PairingPhase phase)
    {
        lock (_gate)
        {
            if (_phase == phase)
            {
                return;
            }

            _phase = phase;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task DiscardAsync(PairingSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.Provider.DeleteKeyAsync(session.Key.Reference);
        }
        catch (Exception e) when (e is DeviceKeyException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Deleting the abandoned device key failed.");
        }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _state = ManagerRegistrationState.Unregistered;
            _phase = null;
            _endpoint = null;
            _stampId = null;
            _fingerprint = null;
            _protection = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RegisterResult Refuse(string code)
    {
        logger.LogInformation("Registration refused: {Code}", code);
        return new RegisterResult(false, code);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
