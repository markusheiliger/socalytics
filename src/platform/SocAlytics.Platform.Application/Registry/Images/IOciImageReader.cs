namespace SocAlytics.Platform.Application.Registry.Images;

/// <summary>Refusal codes of the Analyst image import (contracts/analyst-manifest-and-packaging.md).</summary>
public static class ImageImportRefusalCodes
{
    public const string ReferenceNotDigest = "reference-not-digest";
    public const string ReferenceNotAllowed = "reference-not-allowed";
    public const string ImageNotFound = "image-not-found";
    public const string RegistryUnavailable = "registry-unavailable";
    public const string DigestMismatch = "digest-mismatch";
    public const string ManifestMissing = "manifest-missing";
    public const string ManifestInvalid = "manifest-invalid";
    public const string LabelsMismatch = "labels-mismatch";
    public const string UnsupportedMediaType = "unsupported-media-type";
}

/// <summary>What was read from an image: the registered digest, the configuration labels, and the manifest file.</summary>
/// <param name="ImageDigest">The referenced digest (the index digest when the reference named an OCI index), as <c>sha256:&lt;hex&gt;</c>.</param>
/// <param name="Labels">The labels of the image configuration.</param>
/// <param name="ManifestJson">The content of <c>opt/socalytics/analyst-manifest.json</c>.</param>
/// <param name="ManifestSha256">The lowercase hex SHA-256 of the manifest file bytes.</param>
public sealed record OciImageContent(
    string ImageDigest,
    IReadOnlyDictionary<string, string> Labels,
    string ManifestJson,
    string ManifestSha256);

public sealed record OciImageReadResult
{
    private OciImageReadResult()
    {
    }

    public OciImageContent? Content { get; private init; }

    public string? RefusalCode { get; private init; }

    public string? Detail { get; private init; }

    public bool IsRefused => RefusalCode is not null;

    public static OciImageReadResult Read(OciImageContent content) => new() { Content = content };

    public static OciImageReadResult Refused(string code, string? detail = null) => new() { RefusalCode = code, Detail = detail };
}

/// <summary>Reads the Analyst manifest from an OCI image by digest without downloading its model layers.</summary>
public interface IOciImageReader
{
    /// <summary>Reads <paramref name="reference"/> (<c>host/repository@sha256:&lt;hex&gt;</c>); import problems are returned as refusals.</summary>
    Task<OciImageReadResult> ReadAsync(string reference, CancellationToken cancellationToken);
}
