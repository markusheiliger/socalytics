using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.Images;

namespace SocAlytics.Platform.Infrastructure.Registry.Oci;

internal sealed class OciImageReader(OciDistributionClient client, IOptions<ImageRegistryOptions> options) : IOciImageReader
{
    public const string ManifestPath = "opt/socalytics/analyst-manifest.json";
    public const long MaxLayerBytes = 2 * 1024 * 1024;
    public const long MaxManifestFileBytes = 1024 * 1024;

    private const string DigestSeparator = "@";

    public async Task<OciImageReadResult> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!TryParseReference(reference, out var repository, out var digest, out var refusal))
        {
            return refusal!;
        }

        try
        {
            var manifestObject = await client.GetManifestAsync(repository, digest, cancellationToken);
            var manifest = ParseJson(manifestObject.Content, "image manifest");
            var imageManifest = manifest;

            if (IsIndex(manifest, manifestObject.MediaType))
            {
                var entryDigest = SelectLinuxAmd64(manifest)
                    ?? throw new OciRefusalException(ImageImportRefusalCodes.ImageNotFound, "The image index has no linux/amd64 manifest.");
                var entryObject = await client.GetManifestAsync(repository, entryDigest, cancellationToken);
                imageManifest = ParseJson(entryObject.Content, "image manifest");
                if (IsIndex(imageManifest, entryObject.MediaType))
                {
                    throw new OciRefusalException(ImageImportRefusalCodes.UnsupportedMediaType, "Nested image indexes are not supported.");
                }
            }

            var configDigest = ReadDigest(imageManifest, "config");
            if (!imageManifest.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() == 0)
            {
                throw new OciRefusalException(ImageImportRefusalCodes.ManifestMissing, "The image has no layers.");
            }

            var lastLayer = layers[layers.GetArrayLength() - 1];
            var layerDigest = ReadDigest(lastLayer, null);
            var layerMediaType = lastLayer.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null;
            var gzip = LayerIsGzip(layerMediaType);

            var configObject = await client.GetConfigBlobAsync(repository, configDigest, cancellationToken);
            var labels = ReadLabels(ParseJson(configObject.Content, "image configuration"));

            var layerObject = await client.GetBlobAsync(repository, layerDigest, MaxLayerBytes, ImageImportRefusalCodes.ManifestMissing, cancellationToken);
            var manifestFile = ExtractManifest(layerObject.Content, gzip);

            return OciImageReadResult.Read(new OciImageContent(
                digest,
                labels,
                System.Text.Encoding.UTF8.GetString(manifestFile),
                OciDistributionClient.DigestOf(manifestFile)["sha256:".Length..]));
        }
        catch (OciRefusalException ex)
        {
            return OciImageReadResult.Refused(ex.Code, ex.Message);
        }
    }

    private bool TryParseReference(string reference, out string repository, out string digest, out OciImageReadResult? refusal)
    {
        repository = digest = string.Empty;
        refusal = null;
        var at = reference?.IndexOf(DigestSeparator, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            refusal = OciImageReadResult.Refused(ImageImportRefusalCodes.ReferenceNotDigest, "The reference must use a sha256 digest, not a tag.");
            return false;
        }

        var name = reference![..at];
        digest = reference[(at + 1)..];
        if (!IsSha256(digest) || name.Contains('@'))
        {
            refusal = OciImageReadResult.Refused(ImageImportRefusalCodes.ReferenceNotDigest, "The reference must end in @sha256:<64 lowercase hex characters>.");
            return false;
        }

        var settings = options.Value;
        var slash = name.IndexOf('/');
        var host = slash < 0 ? name : name[..slash];
        repository = slash < 0 ? string.Empty : name[(slash + 1)..];
        var colon = repository.LastIndexOf(':');
        if (colon >= 0 && colon > repository.LastIndexOf('/'))
        {
            refusal = OciImageReadResult.Refused(ImageImportRefusalCodes.ReferenceNotDigest, "The reference must not combine a tag with a digest.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.RegistryHost)
            || !string.Equals(host, settings.RegistryHost, StringComparison.OrdinalIgnoreCase)
            || repository.Length == 0
            || repository.Contains("..", StringComparison.Ordinal)
            || !repository.StartsWith(settings.RepositoryPrefix, StringComparison.Ordinal))
        {
            refusal = OciImageReadResult.Refused(ImageImportRefusalCodes.ReferenceNotAllowed, "The reference is outside the configured registry host and repository prefix.");
            return false;
        }

        return true;
    }

    private static bool IsSha256(string value) =>
        value.Length == "sha256:".Length + 64
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan("sha256:".Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static JsonElement ParseJson(byte[] content, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.ManifestInvalid, $"The {what} is not valid JSON.");
        }
    }

    private static bool IsIndex(JsonElement manifest, string? headerMediaType)
    {
        var mediaType = manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("mediaType", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : headerMediaType;
        return mediaType is "application/vnd.oci.image.index.v1+json" or "application/vnd.docker.distribution.manifest.list.v2+json"
            || (manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("manifests", out _));
    }

    private static string? SelectLinuxAmd64(JsonElement index)
    {
        if (!index.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in manifests.EnumerateArray())
        {
            if (entry.TryGetProperty("platform", out var platform)
                && platform.TryGetProperty("os", out var os) && os.GetString() == "linux"
                && platform.TryGetProperty("architecture", out var arch) && arch.GetString() == "amd64"
                && entry.TryGetProperty("digest", out var d) && d.GetString() is { } value && IsSha256(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadDigest(JsonElement parent, string? property)
    {
        var element = parent;
        if (property is not null && !parent.TryGetProperty(property, out element))
        {
            throw new OciRefusalException(ImageImportRefusalCodes.ManifestInvalid, $"The image manifest has no {property}.");
        }

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("digest", out var digest)
            || digest.GetString() is not { } value
            || !IsSha256(value))
        {
            throw new OciRefusalException(ImageImportRefusalCodes.ManifestInvalid, "The image manifest references a descriptor without a sha256 digest.");
        }

        return value;
    }

    private static bool LayerIsGzip(string? mediaType) => mediaType switch
    {
        "application/vnd.oci.image.layer.v1.tar" => false,
        "application/vnd.oci.image.layer.v1.tar+gzip" => true,
        "application/vnd.docker.image.rootfs.diff.tar.gzip" => true,
        "application/vnd.docker.image.rootfs.diff.tar" => false,
        _ => throw new OciRefusalException(ImageImportRefusalCodes.UnsupportedMediaType, $"The last layer media type '{mediaType}' is not supported."),
    };

    private static Dictionary<string, string> ReadLabels(JsonElement configuration)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configuration.ValueKind == JsonValueKind.Object
            && configuration.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("Labels", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in values.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    labels[label.Name] = label.Value.GetString()!;
                }
            }
        }

        return labels;
    }

    private static byte[] ExtractManifest(byte[] layer, bool gzip)
    {
        try
        {
            using var source = new MemoryStream(layer);
            using var decompressed = gzip ? new GZipStream(source, CompressionMode.Decompress) : (Stream)source;
            using var limited = new LimitedReadStream(decompressed, MaxLayerBytes * 8);
            using var reader = new TarReader(limited);

            byte[]? file = null;
            while (reader.GetNextEntry() is { } entry)
            {
                var name = entry.Name.TrimStart('.').Trim('/');
                if (entry.EntryType == TarEntryType.Directory && (ManifestPath.StartsWith(name + "/", StringComparison.Ordinal) || name.Length == 0))
                {
                    continue;
                }

                if (file is not null || name != ManifestPath || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new OciRefusalException(ImageImportRefusalCodes.ManifestMissing, $"The last layer contains '{entry.Name}' besides {ManifestPath}.");
                }

                if (entry.Length > MaxManifestFileBytes || entry.DataStream is null)
                {
                    throw new OciRefusalException(ImageImportRefusalCodes.ManifestMissing, "The manifest file is empty or too large.");
                }

                using var buffer = new MemoryStream();
                entry.DataStream.CopyTo(buffer);
                file = buffer.ToArray();
            }

            return file ?? throw new OciRefusalException(ImageImportRefusalCodes.ManifestMissing, $"The last layer does not contain {ManifestPath}.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
        {
            throw new OciRefusalException(ImageImportRefusalCodes.ManifestMissing, $"The last layer is not a readable tar archive: {ex.Message}");
        }
    }

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining + 1));
            _remaining -= read;
            if (_remaining < 0)
            {
                throw new InvalidDataException("The layer expands beyond the allowed size.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
