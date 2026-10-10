namespace SocAlytics.Platform.Application.Recordings;

public enum RecordingRetryOperation
{
    StartUpload,
    CompleteUpload,
    ReviseTimelineMapping,
    FinalizeRecordingSet,
}

public static class RecordingRetryOperationExtensions
{
    public static string ToWireValue(this RecordingRetryOperation operation) => operation switch
    {
        RecordingRetryOperation.StartUpload => "start-upload",
        RecordingRetryOperation.CompleteUpload => "complete-upload",
        RecordingRetryOperation.ReviseTimelineMapping => "revise-timeline-mapping",
        RecordingRetryOperation.FinalizeRecordingSet => "finalize-recording-set",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };
}
