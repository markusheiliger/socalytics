using Microsoft.Extensions.Logging;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>
/// Supplies the DPoP session for work operations while the registration is Active. The access token and the
/// opened device key stay in memory only; <see cref="Current"/> is null as soon as the registration is not Active.
/// The acquisition loop refreshes it, so an idle Manager makes no token requests.
/// </summary>
public sealed class WorkSessionProvider(
    RegistrationCoordinator coordinator, ManagerTokenCache tokens, IEnumerable<IDeviceKeyProvider> providers,
    ILogger<WorkSessionProvider> logger)
{
    private readonly IReadOnlyList<IDeviceKeyProvider> _providers = providers.ToList();
    private readonly object _gate = new();
    private PlatformSession? _session;
    private string? _managerId;
    private IDeviceKey? _key;

    public PlatformSession? Current
    {
        get
        {
            if (coordinator.State != ManagerRegistrationState.Active)
            {
                return null;
            }

            lock (_gate) { return _session; }
        }
    }

    /// <summary>Re-evaluates the session; a refused or unavailable token leaves no session.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (coordinator.State != ManagerRegistrationState.Active || coordinator.Active is not { } active)
        {
            Reset();
            return;
        }

        try
        {
            IDeviceKey? key;
            lock (_gate) { key = _key; }
            if (key is null || _managerId != active.ManagerId)
            {
                var provider = _providers.FirstOrDefault(p => p.Id == active.KeyReference.Provider);
                if (provider is null)
                {
                    Reset();
                    return;
                }

                key = await provider.OpenKeyAsync(active.KeyReference, cancellationToken);
            }

            var (signal, token) = await tokens.AcquireAccessTokenAsync(active.PlatformEndpoint, key, active.ManagerId, cancellationToken);
            lock (_gate)
            {
                _key = key;
                _managerId = active.ManagerId;
                _session = signal.Outcome == PlatformOutcome.Success && token is not null
                    ? new PlatformSession(active.PlatformEndpoint, key, token)
                    : null;
            }
        }
        catch (Exception e) when (e is DeviceKeyException or HttpRequestException or PlatformException)
        {
            logger.LogWarning("Refreshing the work session failed: {Error}", e.GetType().Name);
            lock (_gate) { _session = null; }
        }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _session = null;
            _key = null;
            _managerId = null;
        }
    }
}
