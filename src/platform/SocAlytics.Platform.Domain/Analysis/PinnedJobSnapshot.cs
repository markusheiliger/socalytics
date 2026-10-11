using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Analysis;

public sealed record PinnedJobSnapshot(
	int MaxAttempts,
	int LeaseSeconds,
	int StaleTimeoutSeconds,
	int ExecutionTimeoutSeconds,
	string AnalystProfileId,
	Sha256Digest AnalystProfileDigest,
	Sha256Digest ImageDigest,
	Sha256Digest? ModelDigest,
	IReadOnlyDictionary<string, string> Resources,
	string SchemaVersion);
