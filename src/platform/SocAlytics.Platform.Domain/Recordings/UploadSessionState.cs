namespace SocAlytics.Platform.Domain.Recordings;

public enum UploadSessionState
{
    Pending,
    Completed,
    Expired,
}

public static class UploadSessionStates
{
    public static string ToStored(this UploadSessionState state) => state switch
    {
        UploadSessionState.Pending => "pending",
        UploadSessionState.Completed => "completed",
        UploadSessionState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static UploadSessionState FromStored(string value) => value switch
    {
        "pending" => UploadSessionState.Pending,
        "completed" => UploadSessionState.Completed,
        "expired" => UploadSessionState.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown upload session state."),
    };
}
