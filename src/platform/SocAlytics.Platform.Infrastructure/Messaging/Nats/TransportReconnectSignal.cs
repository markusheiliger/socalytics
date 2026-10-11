namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

/// <summary>Raised whenever the transport connection becomes connected (initially and after every reconnect).</summary>
internal sealed class TransportReconnectSignal
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public event Action? Raised;

    public void Raise()
    {
        Interlocked.Increment(ref _count);
        Raised?.Invoke();
    }
}
