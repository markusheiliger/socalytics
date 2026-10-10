using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>Restores the registration on every start or fails closed; it never recreates or rebinds a key.</summary>
public sealed class RegistrationRestorer(
    RegistrationCoordinator coordinator,
    SignedStateStore store,
    ManagerTokenCache tokens,
    IEnumerable<IDeviceKeyProvider> providers,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<RegistrationRestorer> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(store.Path))
        {
            return;
        }

        coordinator.BeginRestore();
        var read = await store.ReadAsync(ct);
        if (read.State is not { } state)
        {
            coordinator.FailRestore(read.Code ?? ManagerDiagnosticCodes.StateUnreadable);
            return;
        }

        var provider = providers.FirstOrDefault(p => p.Id == state.DeviceKey.Provider);
        if (provider is null)
        {
            coordinator.FailRestore(ManagerDiagnosticCodes.KeyMissing);
            return;
        }

        IDeviceKey key;
        try
        {
            key = await provider.OpenKeyAsync(state.DeviceKey, ct);
        }
        catch (DeviceKeyException e)
        {
            coordinator.FailRestore(e.Code == ManagerDiagnosticCodes.KeyMissing
                ? ManagerDiagnosticCodes.KeyMissing
                : ManagerDiagnosticCodes.KeyInaccessible);
            return;
        }

        coordinator.BeginRestore(state);
        var endpoint = new Uri(state.PlatformEndpoint);
        var delay = options.Value.RetryBackoff.Initial;
        while (!ct.IsCancellationRequested)
        {
            SelfRegistrationResult? result = null;
            try
            {
                result = await tokens.GetSelfRegistrationAsync(endpoint, key, state.ManagerId, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogDebug("Platform unreachable during restore.");
            }

            switch (result?.Signal.Outcome)
            {
                case PlatformOutcome.Success:
                    await ConfirmAsync(state, key, result.Registration!, ct);
                    return;
                case PlatformOutcome.Inactive:
                    await ClearAsync(provider, state);
                    return;
            }

            var next = time.GetUtcNow() + delay;
            coordinator.SetConnectivity(new ConnectivityStatus("disconnected", null, next));
            await Task.Delay(delay, time, ct);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.Value.RetryBackoff.Max.Ticks));
        }
    }

    private async Task ConfirmAsync(ProtectedRegistrationState state, IDeviceKey key, OwnRegistration own, CancellationToken ct)
    {
        if (own.StampId != state.StampId)
        {
            coordinator.FailRestore(ManagerDiagnosticCodes.StampMismatch);
            return;
        }

        if (own.KeyThumbprint != state.DeviceKeyThumbprint)
        {
            coordinator.FailRestore(ManagerDiagnosticCodes.KeyMismatch);
            return;
        }

        switch (own.State)
        {
            case "active":
                coordinator.ConfirmRestored(state, key, own, EffectiveIntent(state.Intent), false);
                break;
            case "unregistering":
                if (!state.PendingUnregister)
                {
                    state = state with { PendingUnregister = true, WrittenAt = time.GetUtcNow() };
                    store.Write(state, key);
                }

                coordinator.ConfirmRestored(state, key, own, OperatingIntent.Running, true);
                break;
            default:
                coordinator.FailRestore(ManagerDiagnosticCodes.PlatformUnreachable);
                break;
        }

        await Task.CompletedTask;
    }

    private OperatingIntent EffectiveIntent(OperatingIntent intent) =>
        intent.Kind == OperatingIntentKind.PausedUntil && intent.EndUtc <= time.GetUtcNow()
            ? OperatingIntent.Running
            : intent;

    private async Task ClearAsync(IDeviceKeyProvider provider, ProtectedRegistrationState state)
    {
        coordinator.EnterRevoked();
        tokens.Clear();
        store.Delete();
        try
        {
            await provider.DeleteKeyAsync(state.DeviceKey);
        }
        catch (Exception e) when (e is DeviceKeyException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Deleting the device key failed.");
        }

        coordinator.ClearIdentity();
    }
}

public sealed class RestoreHostedService(RegistrationRestorer restorer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => restorer.RunAsync(stoppingToken), stoppingToken);
}
