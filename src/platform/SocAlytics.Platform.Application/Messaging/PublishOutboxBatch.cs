using System.Text;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.Messaging;

public sealed class PublishOutboxBatch(
    IUnitOfWork unitOfWork,
    IOutboxPublicationStore store,
    IMessageTransport transport,
    IOptions<OutboxPublicationOptions> options,
    TimeProvider timeProvider)
{
    /// <summary>Publishes one batch of due rows and returns the number of rows claimed.</summary>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var rows = await store.ClaimDueAsync(timeProvider.GetUtcNow(), settings.BatchSize, cancellationToken);
        foreach (var row in rows)
        {
            var messageId = row.PublicationRound <= 1 ? row.Id.ToString() : $"{row.Id}.r{row.PublicationRound}";
            try
            {
                await transport.PublishAsync(
                    new TransportMessage(row.Subject, messageId, Encoding.UTF8.GetBytes(row.Payload)), cancellationToken);
                await store.MarkPublishedAsync(row.Id, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var category = ex is MessageTransportException mte
                    ? mte.FailureCategory
                    : ex is OperationCanceledException ? TransportFailureCategories.PublishTimeout
                    : TransportFailureCategories.PublishRejected;
                var now = timeProvider.GetUtcNow();
                var attempt = row.AttemptCount + 1;
                var failed = attempt >= settings.MaxAttempts;
                await store.MarkAttemptFailedAsync(
                    row.Id, now, category, failed ? null : now + OutboxRetryPolicy.Delay(settings, attempt), failed, cancellationToken);
            }
        }

        await scope.CommitAsync(cancellationToken);
        return rows.Count;
    }
}
