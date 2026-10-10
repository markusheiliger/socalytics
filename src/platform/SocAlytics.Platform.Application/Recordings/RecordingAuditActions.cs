using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Recordings;

public static class RecordingAuditActions
{
    public const string StartUploadEvent = "recording.upload.start";
    public const string IssueGrantsEvent = "recording.upload.grant";
    public const string CompleteUploadEvent = "recording.upload.complete";
    public const string ExpireUploadEvent = "recording.upload.expire";
    public const string ReviseTimelineMappingEvent = "recording.timeline-mapping.revise";
    public const string FinalizeRecordingSetEvent = "recording.set.finalize";

    public const string StartUploadAction = "start-upload";
    public const string IssueGrantsAction = "issue-grants";
    public const string CompleteUploadAction = "complete-upload";
    public const string ReviseTimelineMappingAction = "revise-timeline-mapping";
    public const string FinalizeRecordingSetAction = "finalize-recording-set";
    public const string ExpireUploadSessionAction = "expire-upload-session";

    public const string UploadSessionResource = "upload-session";
    public const string RecordingVersionResource = "recording-version";
    public const string TimelineMappingResource = "timeline-mapping";
    public const string RecordingSetVersionResource = "recording-set-version";

    public const string MatchIdDetail = "matchId";
    public const string PartCountDetail = "partCount";
    public const string GrantedPartCountDetail = "grantedPartCount";
    public const string GrantExpiresAtDetail = "grantExpiresAt";
    public const string MemberCountDetail = "memberCount";

    public static AuditResource UploadSession(Guid id) => new(UploadSessionResource, id.ToString());

    public static AuditResource RecordingVersion(Guid id) => new(RecordingVersionResource, id.ToString());

    public static AuditResource TimelineMapping(Guid id) => new(TimelineMappingResource, id.ToString());

    public static AuditResource RecordingSetVersion(Guid id) => new(RecordingSetVersionResource, id.ToString());
}
