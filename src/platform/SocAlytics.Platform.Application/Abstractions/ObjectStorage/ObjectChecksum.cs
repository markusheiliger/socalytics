using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

/// <summary>The full-object SHA-256 and length attested by the store.</summary>
public sealed record ObjectChecksum(Sha256Digest Digest, long ContentLength);
