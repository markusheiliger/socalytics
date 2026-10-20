namespace SocAlytics.Platform.Application.Analysis.Stores;

public interface IPublicationReconciliationStore
{
    Task<IReadOnlyList<Guid>> ListRunsWithReadyJobsAsync(Guid after, int batchSize, CancellationToken cancellationToken);

    /// <summary>Resets the current-occurrence ready-work messages of the run's currently ready jobs. The run must be locked.</summary>
    Task<int> ResetReadyWorkAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Discards the run's unpublished (pending or failed) ready-work messages and returns how many changed. The run must be locked.</summary>
    Task<int> DiscardReadyWorkAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListNotificationsAsync(Guid after, int batchSize, CancellationToken cancellationToken);

    Task<int> ResetMessagesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}
