namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

/// <summary>A presigned single-object read; the URL needs no further headers.</summary>
public sealed record ObjectReadGrant(Uri Url, DateTimeOffset ExpiresAt);
