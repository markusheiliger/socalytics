using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

public sealed record StoredPart(int PartNumber, string ETag, long SizeBytes);

public sealed record CompletedPartEntry(int PartNumber, string ETag, Sha256Digest Digest);
