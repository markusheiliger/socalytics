namespace SocAlytics.Platform.Application.Abstractions;

public interface IOutbox
{
    /// <summary>Enqueues within the active unit of work; false when (MessageType, CausationId) already exists.</summary>
    Task<bool> EnqueueAsync(OutboxMessageDraft draft, CancellationToken cancellationToken);
}
