using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>Polls the platform registration and applies revocation only on an explicit inactive signal.</summary>
public sealed class RegistrationStatusMonitor : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RevocationStopBound = TimeSpan.FromSeconds(8);
    private readonly RegistrationCoordinator _coordinator;
    private readonly ManagerTokenCache _tokens;
    private readonly SignedStateStore _store;
    private readonly IReadOnlyList<IDeviceKeyProvider> _providers;
    private readonly ProtectedStoreFailureHandler _failures;
    private readonly PlatformOptions _options;
    private readonly StatusReportBuilder _reports;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationStatusMonitor> _logger;
    private readonly AttemptRegistry _attempts;
    private readonly QueueCredentialProvider _queueCredentials;
    private readonly IQueueConnection _queue;
    private TimeSpan _backoff;

    public RegistrationStatusMonitor(
        RegistrationCoordinator coordinator, ManagerTokenCache tokens, SignedStateStore store,
        IEnumerable<IDeviceKeyProvider> providers, ProtectedStoreFailureHandler failures, InactiveSignalSink sink,
        IOptions<PlatformOptions> options, StatusReportBuilder reports, TimeProvider time, ILogger<RegistrationStatusMonitor> logger,
        AttemptRegistry attempts, QueueCredentialProvider queueCredentials, IQueueConnection queue)
    {
        _attempts = attempts;
        _queueCredentials = queueCredentials;
        _queue = queue;
        _coordinator = coordinator;
        _tokens = tokens;
        _store = store;
        _providers = providers.ToList();
        _failures = failures;
        _options = options.Value;
        _reports = reports;
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
            var report = _coordinator.State == ManagerRegistrationState.Active ? _reports.Build() : null;
            var result = await _tokens.GetSelfRegistrationAsync(
                active.PlatformEndpoint, key, active.ManagerId, ct, report);
            switch (result.Signal.Outcome)
            {
                case PlatformOutcome.Success:
                    _backoff = _options.RetryBackoff.Initial;
                    if (result.Registration!.ReportAccepted == false)
                    {
                        _logger.LogWarning(
                            "The platform refused the status report: {Violations}.",
                            string.Join(", ", (result.Registration.ReportViolations ?? []).Select(v => $"{v.Field}:{v.Code}")));
                    }

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
        await StopWorkAsync();
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

    /// <summary>Stops every attempt without reporting, discards queue credentials, and closes the queue connection.</summary>
    private async Task StopWorkAsync()
    {
        using var bound = new CancellationTokenSource(RevocationStopBound);
        try
        {
            await _attempts.StopAsync(AttemptStopReason.Revoked, bound.Token);
        }
        catch (OperationCanceledException)
        {
        }

        _queueCredentials.Discard("revoked");
        try
        {
            await _queue.CloseAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("Closing the queue connection failed: {Error}", e.GetType().Name);
        }
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
