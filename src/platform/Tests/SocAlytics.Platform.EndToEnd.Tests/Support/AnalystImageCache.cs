namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>The identity of one cached image: the helper, its mode or variant, and the inputs the image is built from.</summary>
internal readonly record struct AnalystImageKey(string Helper, string Variant, string Inputs);

/// <summary>
/// Builds and pushes each image variant once per test class. Concurrent requests for one key share a single build; a failed
/// or abandoned build is removed so a later test may retry. The cache never acquires the build gate itself: the build
/// delegate (the helper's uncached build and push) does.
/// </summary>
internal sealed class AnalystImageCache : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<AnalystImageKey, Entry> _entries = [];
    private readonly Dictionary<AnalystImageKey, int> _buildCounts = [];
    private readonly CancellationTokenSource _disposed = new();
    private readonly ImageBuildGate _gate;
    private readonly List<Task> _builds = [];

    public AnalystImageCache(ImageBuildGate gate)
    {
        _gate = gate;
    }

    /// <summary>The number of builds started for <paramref name="key"/>, including failed and abandoned ones.</summary>
    public int BuildCount(AnalystImageKey key)
    {
        lock (_lock)
        {
            return _buildCounts.GetValueOrDefault(key);
        }
    }

    /// <summary>Whether an entry (building or ready) currently exists for <paramref name="key"/>.</summary>
    public bool Contains(AnalystImageKey key)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Returns the image of <paramref name="key"/>, starting <paramref name="build"/> only when no entry exists. The build runs under the
    /// cache's own token, so cancelling <paramref name="cancellationToken"/> only stops this caller's wait, unless it was the last waiter.
    /// </summary>
    public async Task<PushedAnalystImage> GetOrBuildAsync(
        AnalystImageKey key,
        Func<CancellationToken, Task<PushedAnalystImage>> build,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Entry entry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token));
                _entries[key] = entry;
                _buildCounts[key] = _buildCounts.GetValueOrDefault(key) + 1;
                entry.Task = RunBuildAsync(key, entry, build);
                _builds.Add(entry.Task);
            }

            entry.Waiters++;
        }

        try
        {
            return await entry.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (--entry.Waiters == 0 && !entry.Task.IsCompleted)
                {
                    // The last waiter left: stop the build and make room for a fresh one.
                    RemoveEntry(key, entry);
                    entry.Cancellation.Cancel();
                }
            }

            throw;
        }
    }

    private async Task<PushedAnalystImage> RunBuildAsync(AnalystImageKey key, Entry entry, Func<CancellationToken, Task<PushedAnalystImage>> build)
    {
        await Task.Yield();
        try
        {
            return await build(entry.Cancellation.Token);
        }
        catch
        {
            lock (_lock)
            {
                RemoveEntry(key, entry);
            }

            throw;
        }
    }

    private void RemoveEntry(AnalystImageKey key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] builds;
        lock (_lock)
        {
            builds = [.. _builds];
        }

        await _disposed.CancelAsync();
        foreach (var build in builds)
        {
            try
            {
                await build;
            }
            catch
            {
                // A failed build was already reported to its waiting tests.
            }
        }

        Dictionary<AnalystImageKey, int> counts;
        lock (_lock)
        {
            counts = new Dictionary<AnalystImageKey, int>(_buildCounts);
        }

        Console.WriteLine(
            "Analyst image builds: "
            + string.Join(", ", counts.Select(c => $"{c.Key.Helper}/{c.Key.Variant}={c.Value}"))
            + $"; gate observed maximum {_gate.ObservedMaximum}");
        _disposed.Dispose();
    }

    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task<PushedAnalystImage> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }
}
