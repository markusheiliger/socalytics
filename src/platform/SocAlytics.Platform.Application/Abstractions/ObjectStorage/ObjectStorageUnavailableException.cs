namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

public sealed class ObjectStorageUnavailableException : Exception
{
    public ObjectStorageUnavailableException(Exception? innerException = null)
        : base("Object storage is unavailable.", innerException)
    {
    }
}
