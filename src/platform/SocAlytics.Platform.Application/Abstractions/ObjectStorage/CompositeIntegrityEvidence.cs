namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

public sealed record CompositeIntegrityEvidence(string? Checksum, string? ChecksumType, long ContentLength, string? ETag);
