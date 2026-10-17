using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>
/// The images this Manager pulled (research.md R10, <c>{StateDirectory}/image-cache.json</c>): a digest is recorded only when it was absent
/// before the pull, so an image the Manager did not pull is never recorded and never removed. Pruning removes recorded images that no active
/// attempt uses, least recently used first while the recorded total exceeds the budget, plus every image idle longer than the configured period.
/// A failed removal stays recorded and is retried by the next pass.
/// </summary>
public sealed class ImageCache
{
    public const string FileName = "image-cache.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<DockerEngineClient?> _engine;
    private readonly string _path;
    private readonly DockerRuntimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _pruneGate = new(1, 1);
    private readonly Dictionary<string, int> _inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingRemoval = new(StringComparer.Ordinal);
    private List<CachedImage>? _images;
    private ImageCacheState? _state;

    public ImageCache(
        Func<DockerEngineClient?> engine, string stateDirectory, DockerRuntimeOptions options, TimeProvider time, ILogger? logger = null)
    {
        _engine = engine;
        _path = Path.Combine(stateDirectory, FileName);
        _options = options;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The result of the last prune; null before the first one.</summary>
    public ImageCacheState? State => _state;

    /// <summary>The digest (<c>sha256:…</c>) of a digest reference, or null when the reference does not pin one.</summary>
    public static string? DigestOf(string imageReference)
    {
        var at = imageReference.IndexOf('@', StringComparison.Ordinal);
        return at < 0 || at == imageReference.Length - 1 ? null : imageReference[(at + 1)..];
    }

    private static string RepositoryOf(string imageReference)
    {
        var at = imageReference.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? imageReference : imageReference[..at];
    }

    /// <summary>Marks the image as used by an active attempt until <see cref="Release"/>; such an image is never pruned.</summary>
    public void Acquire(string imageReference)
    {
        if (DigestOf(imageReference) is not { } digest)
        {
            return;
        }

        lock (_lock)
        {
            _inUse[digest] = _inUse.GetValueOrDefault(digest) + 1;
        }
    }

    /// <summary>Ends the use started by <see cref="Acquire"/> and counts it as the image's last use.</summary>
    public void Release(string imageReference)
    {
        if (DigestOf(imageReference) is not { } digest)
        {
            return;
        }

        lock (_lock)
        {
            if (_inUse.TryGetValue(digest, out var count))
            {
                if (count <= 1)
                {
                    _inUse.Remove(digest);
                }
                else
                {
                    _inUse[digest] = count - 1;
                }
            }

            if (Load().Find(i => i.Digest == digest) is { } image)
            {
                image.LastUsedAt = _time.GetUtcNow();
                Save();
            }
        }
    }

    /// <summary>
    /// Makes the image available: a cached digest is reused without a pull; otherwise the image is pulled and recorded
    /// only when it was absent before this Manager pulled it.
    /// </summary>
    public async Task EnsurePulledAsync(DockerEngineClient engine, string imageReference, string? registryToken, CancellationToken cancellationToken)
    {
        var digest = DigestOf(imageReference);
        var existing = await engine.InspectImageAsync(imageReference, cancellationToken);
        if (existing is not null)
        {
            if (digest is not null)
            {
                lock (_lock)
                {
                    if (Load().Find(i => i.Digest == digest) is { } image)
                    {
                        image.LastUsedAt = _time.GetUtcNow();
                        Save();
                    }
                }
            }

            return;
        }

        await engine.PullImageAsync(imageReference, registryToken, cancellationToken);
        if (digest is null)
        {
            return;
        }

        var pulled = await engine.InspectImageAsync(imageReference, cancellationToken);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (Load().Find(i => i.Digest == digest) is { } image)
            {
                image.LastUsedAt = now;
            }
            else
            {
                Load().Add(new CachedImage
                {
                    Digest = digest, Repository = RepositoryOf(imageReference), SizeBytes = pulled?.Size ?? 0, PulledAt = now, LastUsedAt = now,
                });
            }

            Save();
        }
    }

    /// <summary>Removes recorded images over the budget or idle too long; failures are kept for the next pass.</summary>
    public async Task<ImageCacheState?> PruneAsync(CancellationToken cancellationToken)
    {
        await _pruneGate.WaitAsync(cancellationToken);
        try
        {
            var engine = _engine();
            if (engine is null)
            {
                return _state;
            }

            var now = _time.GetUtcNow();
            List<CachedImage> planned;
            lock (_lock)
            {
                planned = Plan(Load(), now);
            }

            var removed = 0;
            var leftovers = new List<string>();
            foreach (var image in planned)
            {
                try
                {
                    await engine.RemoveImageAsync($"{image.Repository}@{image.Digest}", cancellationToken);
                    lock (_lock)
                    {
                        Load().RemoveAll(i => i.Digest == image.Digest);
                        _pendingRemoval.Remove(image.Digest);
                        Save();
                    }

                    removed++;
                }
                catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException
                    || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogWarning("Removing a cached image failed: {Error}", e.GetType().Name);
                    lock (_lock)
                    {
                        _pendingRemoval.Add(image.Digest);
                    }

                    leftovers.Add($"image {image.Digest}");
                }
            }

            lock (_lock)
            {
                var images = Load();
                _state = new ImageCacheState(now, images.Count, images.Sum(i => i.SizeBytes), _options.ImageCacheBudgetBytes, removed, leftovers);
                return _state;
            }
        }
        finally
        {
            _pruneGate.Release();
        }
    }

    // Least recently used first: failed removals of earlier passes and idle images are always planned, then the oldest until within budget.
    private List<CachedImage> Plan(List<CachedImage> images, DateTimeOffset now)
    {
        var candidates = images.Where(i => !_inUse.ContainsKey(i.Digest)).OrderBy(i => i.LastUsedAt).ToList();
        var planned = candidates
            .Where(i => _pendingRemoval.Contains(i.Digest) || now - i.LastUsedAt > _options.ImageCacheMaxIdle)
            .ToList();
        var total = images.Except(planned).Sum(i => i.SizeBytes);
        foreach (var candidate in candidates.Except(planned))
        {
            if (total <= _options.ImageCacheBudgetBytes)
            {
                break;
            }

            planned.Add(candidate);
            total -= candidate.SizeBytes;
        }

        return [.. planned.OrderBy(i => i.LastUsedAt)];
    }

    private List<CachedImage> Load()
    {
        if (_images is not null)
        {
            return _images;
        }

        _images = [];
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path), Json);
                if (file is { Version: FormatVersion, Images: { } images })
                {
                    _images = [.. images.Where(i => !string.IsNullOrEmpty(i.Digest) && !string.IsNullOrEmpty(i.Repository))];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("The image cache record could not be read; starting empty: {Error}", e.GetType().Name);
        }

        return _images;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CacheFile { Version = FormatVersion, Images = _images ?? [] }, Json));
            File.Move(temporary, _path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The image cache record could not be written: {Error}", e.GetType().Name);
        }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }

        public List<CachedImage> Images { get; set; } = [];
    }

    private sealed class CachedImage
    {
        public string Digest { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public DateTimeOffset PulledAt { get; set; }

        public DateTimeOffset LastUsedAt { get; set; }
    }
}
