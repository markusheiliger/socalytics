namespace SocAlytics.Platform.Application.Abstractions;

public static class TransportFailureCategories
{
    public const string TransportUnavailable = "transport-unavailable";
    public const string PublishTimeout = "publish-timeout";
    public const string PublishRejected = "publish-rejected";
}

public sealed class MessageTransportException(string failureCategory, Exception? innerException = null)
    : Exception(failureCategory, innerException)
{
    public string FailureCategory { get; } = failureCategory;
}
