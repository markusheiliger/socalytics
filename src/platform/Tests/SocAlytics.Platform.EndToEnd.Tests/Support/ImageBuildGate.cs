namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>Bounds the builds and pushes that run at the same time and records the highest concurrency it observed.</summary>
internal sealed class ImageBuildGate
{
    private readonly SemaphoreSlim _semaphore;
    private int _current;
    private int _observedMaximum;

    public ImageBuildGate(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Limit = limit;
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    public int Limit { get; }

    public int ObservedMaximum => Volatile.Read(ref _observedMaximum);

    /// <summary>Waits for a slot with the caller's token; dispose the returned lease in <c>finally</c> to release it.</summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);

        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _observedMaximum)))
        {
            if (Interlocked.CompareExchange(ref _observedMaximum, now, seen) == seen)
            {
                break;
            }
        }

        return new Lease(this);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _current);
        _semaphore.Release();
    }

    private sealed class Lease(ImageBuildGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
