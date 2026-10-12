using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.Messaging;

public interface IOutboxRetentionStore
{
    /// <summary>Deletes up to <paramref name="batchSize"/> eligible rows and returns the number deleted.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);
}

public sealed class PurgeOutboxMessagesHandler(
    IUnitOfWork unitOfWork,
    IOutboxRetentionStore store,
    IOptions<OutboxPublicationOptions> options,
    TimeProvider timeProvider)
{
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromHours(settings.RetentionPeriodHours);
        var batch = Math.Max(1, settings.RetentionBatchSize);
        var total = 0;
        while (true)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var deleted = await store.DeleteExpiredAsync(cutoff, batch, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            total += deleted;
            if (deleted < batch)
            {
                return total;
            }
        }
    }
}
