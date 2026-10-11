using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>In-memory only; the code, handle, and challenge are never persisted, logged, or put in a status report.</summary>
internal sealed class PairingSession(
    Uri endpoint, IDeviceKeyProvider provider, IDeviceKey key, string pollingHandle, string verificationUri,
    string pairingCode, DateTimeOffset expiresAt, TimeSpan pollingInterval)
{
    public Uri Endpoint { get; } = endpoint;

    public IDeviceKeyProvider Provider { get; } = provider;

    public IDeviceKey Key { get; } = key;

    public string PollingHandle { get; } = pollingHandle;

    public string VerificationUri { get; } = verificationUri;

    public string PairingCode { get; } = pairingCode;

    public DateTimeOffset ExpiresAt { get; set; } = expiresAt;

    public TimeSpan PollingInterval { get; set; } = pollingInterval;

    public string? Challenge { get; set; }

    public override string ToString() => nameof(PairingSession);
}
