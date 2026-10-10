using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Platform;

public sealed record SelfRegistrationResult(PlatformSignal Signal, OwnRegistration? Registration);

/// <summary>Holds the short-lived access token in memory only; it is never persisted or logged.</summary>
public sealed class ManagerTokenCache(
    PlatformClient platform, TimeProvider time, Registration.ProtectedStoreFailureHandler? failures = null)
{
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _validUntil;

    public void Clear()
    {
        lock (_gate) { _token = null; }
    }

    public async Task<SelfRegistrationResult> GetSelfRegistrationAsync(
        Uri endpoint, IDeviceKey key, string managerId, CancellationToken cancellationToken)
    {
        try
        {
            return await GetSelfRegistrationCoreAsync(endpoint, key, managerId, cancellationToken);
        }
        catch (DeviceKeyException e) when (failures?.Handle(e.Code) == true)
        {
            Clear();
            return new(new PlatformSignal(PlatformOutcome.Refused), null);
        }
    }

    /// <summary>Begins (<paramref name="completion"/> false) or completes the platform unregistration.</summary>
    public async Task<PlatformSignal> PostUnregistrationAsync(
        Uri endpoint, IDeviceKey key, string managerId, bool completion, CancellationToken cancellationToken)
    {
        var (signal, token) = await GetTokenAsync(endpoint, key, managerId, cancellationToken);
        if (token is null)
        {
            return signal;
        }

        var request = completion
            ? platform.BuildUnregistrationCompletion(endpoint, key, token)
            : platform.BuildUnregistration(endpoint, key, token);
        var response = await platform.SendAsync(request, cancellationToken);
        signal = PlatformSignals.ClassifyResource(response.Status, response.WwwAuthenticate, response.Body);
        if (signal.Outcome != PlatformOutcome.Success)
        {
            Clear();
        }

        return signal;
    }

    private async Task<SelfRegistrationResult> GetSelfRegistrationCoreAsync(
        Uri endpoint, IDeviceKey key, string managerId, CancellationToken cancellationToken)
    {
        var (signal, token) = await GetTokenAsync(endpoint, key, managerId, cancellationToken);
        if (token is null)
        {
            return new(signal, null);
        }

        var response = await platform.SendAsync(platform.BuildSelfRegistration(endpoint, key, token), cancellationToken);
        signal = PlatformSignals.ClassifyResource(response.Status, response.WwwAuthenticate, response.Body);
        if (signal.Outcome != PlatformOutcome.Success)
        {
            Clear();
            return new(signal, null);
        }

        var own = response.Read<OwnRegistration>();
        return own is null ? new(new PlatformSignal(PlatformOutcome.Refused), null) : new(signal, own);
    }

    private async Task<(PlatformSignal, string?)> GetTokenAsync(
        Uri endpoint, IDeviceKey key, string managerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_token is not null && time.GetUtcNow() < _validUntil)
            {
                return (new PlatformSignal(PlatformOutcome.Success), _token);
            }
        }

        var response = await platform.SendAsync(platform.BuildToken(endpoint, key, managerId), cancellationToken);
        var signal = PlatformSignals.ClassifyToken(response.Status, response.Body);
        if (signal.Outcome != PlatformOutcome.Success)
        {
            return (signal, null);
        }

        var token = response.Read<TokenResponse>();
        if (token is null || !string.Equals(token.TokenType, "DPoP", StringComparison.OrdinalIgnoreCase))
        {
            return (new PlatformSignal(PlatformOutcome.Refused), null);
        }

        var margin = Math.Min(30, token.ExpiresIn / 2);
        lock (_gate)
        {
            _token = token.AccessToken;
            _validUntil = time.GetUtcNow().AddSeconds(token.ExpiresIn - margin);
        }

        return (signal, token.AccessToken);
    }
}
