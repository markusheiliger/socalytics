namespace SocAlytics.Platform.Domain.Recordings;

public sealed record RecordingSetMember(int Position, Guid RecordingVersionId, Guid TimelineMappingId);
