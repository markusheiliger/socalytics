using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

public sealed record PublishedImage(string Repository, string ManifestDigest, string ConfigDigest, string FillerDigest, string ManifestLayerDigest)
{
    public string Reference(string registryHost) => $"{registryHost}/{Repository}@{ManifestDigest}";
}

public sealed record TestImageSpec
{
    public required string Repository { get; init; }

    public byte[] ManifestFile { get; init; } = Encoding.UTF8.GetBytes("{\"manifestVersion\":\"1.0.0\"}");

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Further files in the manifest layer; the layer must hold only the manifest file otherwise.</summary>
    public IReadOnlyList<string> ExtraLayerFiles { get; init; } = [];

    public string ManifestPath { get; init; } = "opt/socalytics/analyst-manifest.json";

    public bool GzipLayer { get; init; } = true;

    public string? LayerMediaType { get; init; }

    public bool WrapInIndex { get; init; }

    /// <summary>Filler standing in for model weights.</summary>
    public int FillerBytes { get; init; } = 256 * 1024;
}

/// <summary>Builds a minimal image (filler layer, manifest layer last, configuration with labels) and pushes it through the Distribution API.</summary>
public sealed class TestImagePublisher(string registryHost) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://{registryHost}") };

    public async Task<PublishedImage> PublishAsync(TestImageSpec spec)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var filler = RandomNumberGenerator.GetBytes(spec.FillerBytes);
        var fillerLayer = Gzip(Tar(("model/weights.bin", filler)));

        var files = new List<(string, byte[])> { (spec.ManifestPath, spec.ManifestFile) };
        files.AddRange(spec.ExtraLayerFiles.Select(name => (name, new byte[] { 1 })));
        var rawManifestLayer = Tar(files.ToArray());
        var manifestLayer = spec.GzipLayer ? Gzip(rawManifestLayer) : rawManifestLayer;

        var config = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["architecture"] = "amd64",
            ["os"] = "linux",
            ["config"] = new Dictionary<string, object> { ["Labels"] = spec.Labels },
            ["rootfs"] = new Dictionary<string, object>
            {
                ["type"] = "layers",
                ["diff_ids"] = new[] { DigestOf(Gunzip(fillerLayer)), DigestOf(rawManifestLayer) },
            },
        });

        var fillerDigest = await PushBlobAsync(spec.Repository, fillerLayer, cancellationToken);
        var layerDigest = await PushBlobAsync(spec.Repository, manifestLayer, cancellationToken);
        var configDigest = await PushBlobAsync(spec.Repository, config, cancellationToken);

        var layerMediaType = spec.LayerMediaType
            ?? (spec.GzipLayer ? "application/vnd.oci.image.layer.v1.tar+gzip" : "application/vnd.oci.image.layer.v1.tar");
        var imageManifest = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = Descriptor("application/vnd.oci.image.config.v1+json", configDigest, config.Length),
            ["layers"] = new[]
            {
                Descriptor("application/vnd.oci.image.layer.v1.tar+gzip", fillerDigest, fillerLayer.Length),
                Descriptor(layerMediaType, layerDigest, manifestLayer.Length),
            },
        });
        var imageDigest = await PushManifestAsync(spec.Repository, imageManifest, "application/vnd.oci.image.manifest.v1+json", cancellationToken);

        var manifestDigest = imageDigest;
        if (spec.WrapInIndex)
        {
            var index = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = "application/vnd.oci.image.index.v1+json",
                ["manifests"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                        ["digest"] = imageDigest,
                        ["size"] = imageManifest.Length,
                        ["platform"] = new Dictionary<string, string> { ["os"] = "linux", ["architecture"] = "amd64" },
                    },
                },
            });
            manifestDigest = await PushManifestAsync(spec.Repository, index, "application/vnd.oci.image.index.v1+json", cancellationToken);
        }

        return new PublishedImage(spec.Repository, manifestDigest, configDigest, fillerDigest, layerDigest);
    }

    public void Dispose() => _http.Dispose();

    public static string DigestOf(byte[] content) => "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static Dictionary<string, object> Descriptor(string mediaType, string digest, int size) =>
        new() { ["mediaType"] = mediaType, ["digest"] = digest, ["size"] = size };

    private async Task<string> PushBlobAsync(string repository, byte[] content, CancellationToken cancellationToken)
    {
        var digest = DigestOf(content);
        using var start = await _http.PostAsync($"/v2/{repository}/blobs/uploads/", null, cancellationToken);
        start.EnsureSuccessStatusCode();
        var location = start.Headers.Location!;
        var target = location.IsAbsoluteUri ? location : new Uri(_http.BaseAddress!, location);
        var separator = string.IsNullOrEmpty(target.Query) ? "?" : "&";
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{target}{separator}digest={Uri.EscapeDataString(digest)}")
        {
            Content = new ByteArrayContent(content),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.SendAsync(put, cancellationToken);
        response.EnsureSuccessStatusCode();
        return digest;
    }

    private async Task<string> PushManifestAsync(string repository, byte[] manifest, string mediaType, CancellationToken cancellationToken)
    {
        var digest = DigestOf(manifest);
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/v2/{repository}/manifests/{digest}") { Content = new ByteArrayContent(manifest) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await _http.SendAsync(put, cancellationToken);
        response.EnsureSuccessStatusCode();
        return digest;
    }

    private static byte[] Tar(params (string Name, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }

        return stream.ToArray();
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] content)
    {
        using var input = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
