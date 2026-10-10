using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>Polls the platform registration and applies revocation only on an explicit inactive signal.</summary>
public sealed class RegistrationStatusMonitor : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private readonly RegistrationCoordinator _coordinator;
    private readonly ManagerTokenCache _tokens;
    private readonly SignedStateStore _store;
    private readonly IReadOnlyList<IDeviceKeyProvider> _providers;
    private readonly ProtectedStoreFailureHandler _failures;
    private readonly PlatformOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationStatusMonitor> _logger;
    private TimeSpan _backoff;

    public RegistrationStatusMonitor(
        RegistrationCoordinator coordinator, ManagerTokenCache tokens, SignedStateStore store,
        IEnumerable<IDeviceKeyProvider> providers, ProtectedStoreFailureHandler failures, InactiveSignalSink sink,
        IOptions<PlatformOptions> options, TimeProvider time, ILogger<RegistrationStatusMonitor> logger)
    {
        _coordinator = coordinator;
        _tokens = tokens;
        _store = store;
        _providers = providers.ToList();
        _failures = failures;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _backoff = _options.RetryBackoff.Initial;
        sink.Subscribe(_ => RevokeAsync());
    }

    private bool Monitored =>
        _coordinator.State is ManagerRegistrationState.Active or ManagerRegistrationState.Unregistering;

    /// <summary>Performs one status check and returns the delay until the next one.</summary>
    public async Task<TimeSpan> CheckOnceAsync(CancellationToken ct)
    {
        if (!Monitored || _coordinator.Active is not { } active)
        {
            return Idle;
        }

        try
        {
            var provider = _providers.FirstOrDefault(p => p.Id == active.KeyReference.Provider);
            if (provider is null)
            {
                _failures.Handle(ManagerDiagnosticCodes.KeyMissing);
                return Idle;
            }

            var key = await provider.OpenKeyAsync(active.KeyReference, ct);
            var result = await _tokens.GetSelfRegistrationAsync(active.PlatformEndpoint, key, active.ManagerId, ct);
            switch (result.Signal.Outcome)
            {
                case PlatformOutcome.Success:
                    _backoff = _options.RetryBackoff.Initial;
                    _coordinator.SetConnectivity(
                        new ConnectivityStatus("connected", _time.GetUtcNow()), result.Registration!.ClubName);
                    return result.Registration.StatusCheckIntervalSeconds is > 0 and var s
                        ? TimeSpan.FromSeconds(s)
                        : _options.StatusCheckInterval;
                case PlatformOutcome.Inactive:
                    await RevokeAsync();
                    return Idle;
            }
        }
        catch (DeviceKeyException e) when (_failures.Handle(e.Code))
        {
            return Idle;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Platform unreachable during status check.");
        }

        if (!Monitored)
        {
            return Idle;
        }

        var delay = _backoff;
        _backoff = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryBackoff.Max.Ticks));
        _coordinator.SetConnectivity(new ConnectivityStatus("disconnected", null, _time.GetUtcNow() + delay));
        return delay;
    }

    public async Task RevokeAsync()
    {
        if (!Monitored || _coordinator.Active is not { } active)
        {
            return;
        }

        _coordinator.EnterRevoked();
        _tokens.Clear();
        _store.Delete();
        try
        {
            var provider = _providers.FirstOrDefault(p => p.Id == active.KeyReference.Provider);
            if (provider is not null)
            {
                await provider.DeleteKeyAsync(active.KeyReference);
            }
        }
        catch (Exception e) when (e is DeviceKeyException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("Deleting the device key failed.");
        }

        _coordinator.ClearIdentity();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(await CheckOnceAsync(stoppingToken), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
