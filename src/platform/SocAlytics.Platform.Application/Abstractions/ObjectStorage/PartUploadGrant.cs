namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

/// <summary>A presigned part upload; the client must send exactly <see cref="RequiredHeaders"/>.</summary>
public sealed record PartUploadGrant(
    int PartNumber,
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);
