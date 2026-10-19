using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Telemetry;

namespace SocAlytics.Platform.Application.Messaging;

public sealed class PublishOutboxBatch(
    IUnitOfWork unitOfWork,
    IOutboxPublicationStore store,
    IMessageTransport transport,
    IOptions<OutboxPublicationOptions> options,
    TimeProvider timeProvider)
{
    public const string AgentInvocationReadyMessageType = "agent-invocation-ready";

    /// <summary>Publishes one batch of due rows and returns the number of rows claimed.</summary>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var rows = await store.ClaimDueAsync(timeProvider.GetUtcNow(), settings.BatchSize, cancellationToken);
        foreach (var row in rows)
        {
            var baseId = row.MessageType == AgentInvocationReadyMessageType && TryGetAgentMessageId(row.Payload, out var agentId)
                ? agentId
                : row.Id.ToString();
            var messageId = row.PublicationRound <= 1 ? baseId : $"{baseId}.r{row.PublicationRound}";
            try
            {
                await transport.PublishAsync(
                    new TransportMessage(row.Subject, messageId, Encoding.UTF8.GetBytes(row.Payload)), cancellationToken);
                var publishedAt = timeProvider.GetUtcNow();
                await store.MarkPublishedAsync(row.Id, publishedAt, cancellationToken);
                MessagingMetrics.RecordPublished(row.Subject, (publishedAt - new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc))).TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var category = ex is MessageTransportException mte
                    ? mte.FailureCategory
                    : ex is OperationCanceledException ? TransportFailureCategories.PublishTimeout
                    : TransportFailureCategories.PublishRejected;
                var now = timeProvider.GetUtcNow();
                MessagingMetrics.RecordFailed(row.Subject, category);
                var attempt = row.AttemptCount + 1;
                var failed = attempt >= settings.MaxAttempts;
                await store.MarkAttemptFailedAsync(
                    row.Id, now, category, failed ? null : now + OutboxRetryPolicy.Delay(settings, attempt), failed, cancellationToken);
            }
        }

        MessagingMetrics.SetBacklog(await store.GetBacklogAsync(timeProvider.GetUtcNow(), cancellationToken));
        await scope.CommitAsync(cancellationToken);
        return rows.Count;
    }

    // The broker deduplicates agent notifications by <invocationId>:<readyOccurrence>.
    private static bool TryGetAgentMessageId(string payload, out string messageId)
    {
        messageId = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("invocationId", out var invocation)
                && invocation.ValueKind == JsonValueKind.String
                && root.TryGetProperty("readyOccurrence", out var occurrence)
                && occurrence.TryGetInt32(out var n))
            {
                messageId = $"{invocation.GetString()}:{n}";
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
