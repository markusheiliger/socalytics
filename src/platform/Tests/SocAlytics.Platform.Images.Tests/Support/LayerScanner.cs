using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SocAlytics.Platform.Images.Tests.Support;

/// <summary>One layer of an image: its files (directories and whiteouts excluded) and the files that hold a private-key PEM block.</summary>
internal sealed record ScannedLayer(int Index, string Path, IReadOnlyList<string> Files, IReadOnlyList<string> PrivateKeyFiles);

/// <summary>An image read from <c>docker save</c> output: its configuration and the files of every layer.</summary>
internal sealed class ScannedImage : IDisposable
{
    private readonly string _directory;

    public ScannedImage(string directory, ImageConfiguration configuration, string configurationJson, IReadOnlyList<ScannedLayer> layers)
    {
        _directory = directory;
        Configuration = configuration;
        ConfigurationJson = configurationJson;
        Layers = layers;
    }

    public ImageConfiguration Configuration { get; }

    /// <summary>The raw image configuration document.</summary>
    public string ConfigurationJson { get; }

    public IReadOnlyList<ScannedLayer> Layers { get; }

    public IEnumerable<string> AllFiles => Layers.SelectMany(layer => layer.Files).Distinct(StringComparer.Ordinal);

    /// <summary>The content of <paramref name="path"/> from the topmost layer that holds it, or <see langword="null"/>.</summary>
    public byte[]? ReadFile(string path)
    {
        foreach (var layer in Layers.Reverse())
        {
            if (!layer.Files.Contains(path, StringComparer.Ordinal))
            {
                continue;
            }

            using var stream = LayerScanner.OpenLayer(System.IO.Path.Combine(_directory, layer.Path));
            using var reader = new TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                if (LayerScanner.Normalize(entry.Name) == path && entry.DataStream is { } data)
                {
                    using var buffer = new MemoryStream();
                    data.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static partial class LayerScanner
{
    private const long MaximumContentScan = 64L * 1024 * 1024;
    private static readonly byte[] PrivateKeyMarker = Encoding.ASCII.GetBytes("PRIVATE KEY-----");

    public static async Task<ScannedImage> ScanAsync(string repositoryRoot, string image)
    {
        var directory = Path.Combine(Path.GetTempPath(), "socalytics-image-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "image.tar");
            var saved = await ImageBuildFixture.RunDockerAsync(repositoryRoot, "save", "--output", archive, image);
            if (saved.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker save of {image} failed with exit code {saved.ExitCode}:\n{saved.Output}");
            }

            var content = Path.Combine(directory, "content");
            Directory.CreateDirectory(content);
            await TarFile.ExtractToDirectoryAsync(archive, content, overwriteFiles: true);
            File.Delete(archive);

            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(content, "manifest.json")));
            var entry = manifest.RootElement[0];
            var configurationJson = await File.ReadAllTextAsync(Path.Combine(content, entry.GetProperty("Config").GetString()!));

            var layers = new List<ScannedLayer>();
            foreach (var layerPath in entry.GetProperty("Layers").EnumerateArray().Select(layer => layer.GetString()!))
            {
                layers.Add(ScanLayer(layers.Count, layerPath, Path.Combine(content, layerPath)));
            }

            return new ScannedImage(content, ParseConfiguration(configurationJson), configurationJson, layers);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal static Stream OpenLayer(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = 0;
        return first == 0x1f && second == 0x8b ? new GZipStream(stream, CompressionMode.Decompress) : stream;
    }

    internal static string Normalize(string name) => "/" + name.TrimStart('.').TrimStart('/');

    private static ScannedLayer ScanLayer(int index, string relativePath, string fullPath)
    {
        var files = new List<string>();
        var keys = new List<string>();
        using var stream = OpenLayer(fullPath);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = Normalize(entry.Name);
            if (entry.EntryType is TarEntryType.Directory || Path.GetFileName(name).StartsWith(".wh.", StringComparison.Ordinal))
            {
                continue;
            }

            files.Add(name);
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && entry.Length is > 0 and <= MaximumContentScan
                && entry.DataStream is { } data)
            {
                var bytes = new byte[entry.Length];
                data.ReadExactly(bytes);
                if (ContainsPrivateKeyBlock(bytes))
                {
                    keys.Add(name);
                }
            }
        }

        return new ScannedLayer(index, relativePath, files, keys);
    }

    private static bool ContainsPrivateKeyBlock(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var found = bytes.AsSpan(offset).IndexOf(PrivateKeyMarker);
            if (found < 0)
            {
                return false;
            }

            var position = offset + found;
            var start = Math.Max(0, position - 30);
            var end = Math.Min(bytes.Length, position + 120);
            if (PemBlockRegex().IsMatch(Encoding.Latin1.GetString(bytes, start, end - start)))
            {
                return true;
            }

            offset = position + PrivateKeyMarker.Length;
        }

        return false;
    }

    private static ImageConfiguration ParseConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var config = root.GetProperty("config");

        var labels = config.TryGetProperty("Labels", out var labelValue) && labelValue.ValueKind == JsonValueKind.Object
            ? labelValue.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty)
            : [];
        var environment = config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array
            ? env.EnumerateArray().Select(item => item.GetString()!).ToList()
            : [];
        var healthcheck = config.TryGetProperty("Healthcheck", out var health) && health.ValueKind == JsonValueKind.Object
            && !(health.TryGetProperty("Test", out var test) && test.ValueKind == JsonValueKind.Array
                && test.GetArrayLength() == 1 && test[0].GetString() == "NONE");
        var history = root.TryGetProperty("history", out var historyValue) && historyValue.ValueKind == JsonValueKind.Array
            ? historyValue.EnumerateArray()
                .Select(item => item.TryGetProperty("created_by", out var createdBy) ? createdBy.GetString() ?? string.Empty : string.Empty)
                .ToList()
            : [];

        return new ImageConfiguration(
            config.TryGetProperty("User", out var user) ? user.GetString() ?? string.Empty : string.Empty,
            labels,
            healthcheck,
            environment,
            history);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----\r?\n[A-Za-z0-9+/=:,\- \r\n]{16,}")]
    private static partial Regex PemBlockRegex();
}

/// <summary>Scans each image once per test class and removes the extracted data afterwards.</summary>
public sealed class LayerScanFixture(ImageBuildFixture images) : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ScannedImage>>> _scans = new();

    internal ImageBuildFixture Images => images;

    internal Task<ScannedImage> ScanAsync(string dockerfile) =>
        _scans.GetOrAdd(dockerfile, key => new Lazy<Task<ScannedImage>>(async () =>
            await LayerScanner.ScanAsync(images.RepositoryRoot, await images.BuildAsync(key)))).Value;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var scan in _scans.Values)
        {
            if (scan.IsValueCreated)
            {
                try
                {
                    (await scan.Value).Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
