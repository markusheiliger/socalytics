namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

public enum ObjectStorageRejectionReason
{
    InvalidPart,
    BadDigest,
    EntityTooSmall,
    NoSuchUpload,
}

public sealed class ObjectStorageRejectedException : Exception
{
    public ObjectStorageRejectedException(ObjectStorageRejectionReason reason, Exception? innerException = null)
        : base($"Object storage rejected the request: {reason}.", innerException) => Reason = reason;

    public ObjectStorageRejectionReason Reason { get; }
}
