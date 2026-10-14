using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>Owns the running attempts: counts them, waits for idle, and stops them for a drain timeout or exit.</summary>
public sealed class AttemptRegistry : IManagerWorkTracker
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private TaskCompletionSource _idle = Completed();

    public int ActiveCount
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    /// <summary>The attempts that were claimed and have not finished.</summary>
    public IReadOnlyList<ActiveAttempt> Attempts
    {
        get { lock (_gate) { return _entries.Select(e => e.Attempt).OfType<ActiveAttempt>().ToList(); } }
    }

    /// <summary>Counts the run immediately; <paramref name="body"/> receives a token that <see cref="StopAndCleanUpAsync"/> cancels.</summary>
    public Task Run(Func<Action<ActiveAttempt>, CancellationToken, Task> body, CancellationToken stopping)
    {
        var entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(stopping));
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _entries.Add(entry);
        }

        entry.Task = Task.Run(async () =>
        {
            try
            {
                await body(attempt =>
                {
                    lock (_gate)
                    {
                        entry.Attempt = attempt;
                        attempt.StopReason = entry.Reason;
                    }
                }, entry.Cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Remove(entry);
            }
        }, CancellationToken.None);
        return entry.Task;
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _entries.Count == 0 ? Task.CompletedTask : _idle.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Stops every attempt for the drain timeout; each current attempt reports a failed completion.</summary>
    public Task StopAndCleanUpAsync(CancellationToken cancellationToken) =>
        StopAsync(AttemptStopReason.DrainTimeout, cancellationToken);

    /// <summary>Stops and cleans up every attempt; only <see cref="AttemptStopReason.DrainTimeout"/> reports a completion.</summary>
    public async Task StopAsync(AttemptStopReason reason, CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
            foreach (var entry in entries)
            {
                entry.Reason = reason;
                if (entry.Attempt is { } attempt)
                {
                    attempt.StopReason = reason;
                }
            }
        }

        foreach (var entry in entries)
        {
            await entry.Cts.CancelAsync();
        }

        try
        {
            await Task.WhenAll(entries.Select(e => e.Task)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
            if (_entries.Count == 0)
            {
                _idle.TrySetResult();
            }
        }

        entry.Cts.Dispose();
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    private sealed class Entry(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Task Task { get; set; } = Task.CompletedTask;

        public ActiveAttempt? Attempt { get; set; }

        public AttemptStopReason? Reason { get; set; }
    }
}
