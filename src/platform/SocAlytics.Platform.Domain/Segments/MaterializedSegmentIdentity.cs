using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Segments;

public sealed record MaterializedSegmentIdentity
{
	public MaterializedSegmentIdentity(
		Guid recordingVersionId,
		Sha256Digest timelineMappingDigest,
		Sha256Digest segmentationPolicyDigest,
		int segmentNumber)
	{
		if (recordingVersionId == Guid.Empty)
		{
			throw new ArgumentException("The recording version is required.", nameof(recordingVersionId));
		}

		ArgumentNullException.ThrowIfNull(timelineMappingDigest);
		ArgumentNullException.ThrowIfNull(segmentationPolicyDigest);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentNumber, 0);

		RecordingVersionId = recordingVersionId;
		TimelineMappingDigest = timelineMappingDigest;
		SegmentationPolicyDigest = segmentationPolicyDigest;
		SegmentNumber = segmentNumber;
	}

	public Guid RecordingVersionId { get; }

	public Sha256Digest TimelineMappingDigest { get; }

	public Sha256Digest SegmentationPolicyDigest { get; }

	public int SegmentNumber { get; }
}
