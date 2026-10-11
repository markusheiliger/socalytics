namespace SocAlytics.Platform.Application.Abstractions;

public interface IMessageTransport
{
    Task<TransportPublishResult> PublishAsync(TransportMessage message, CancellationToken cancellationToken);
}

public sealed record TransportMessage(string Subject, string MessageId, ReadOnlyMemory<byte> Payload);

public enum TransportPublishResult
{
    Published,
    Duplicate,
}
