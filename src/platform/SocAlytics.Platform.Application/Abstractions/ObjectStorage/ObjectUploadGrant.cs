namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

/// <summary>A presigned single-object upload; the client must send exactly <see cref="RequiredHeaders"/>.</summary>
public sealed record ObjectUploadGrant(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);
