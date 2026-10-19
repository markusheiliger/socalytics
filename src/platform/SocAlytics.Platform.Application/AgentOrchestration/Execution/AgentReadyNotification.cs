using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

internal static class AgentReadyNotification
{
    // Writes an agent-invocation-ready outbox message for the given occurrence inside the active unit of work.
    public static async Task EnqueueAsync(
        IOutbox outbox,
        IContractValidator validator,
        IQueueCredentialIssuer queue,
        AgentInvocationRecord invocation,
        int readyOccurrence,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0.0",
            invocationId = invocation.Id,
            route = AgentPersistedNames.ToPersistedName(invocation.Route),
            agentId = invocation.AgentId,
            readyOccurrence,
            correlationId = invocation.CorrelationId,
        });
        if (!validator.Validate(ContractSchemaIds.AgentWorkNotificationV1, payload).IsValid)
        {
            throw new InvalidOperationException("The work notification does not conform to work-notification/v1.");
        }

        await outbox.EnqueueAsync(
            new OutboxMessageDraft(
                Guid.CreateVersion7(), PublishOutboxBatch.AgentInvocationReadyMessageType, "1.0.0", queue.AgentWorkSubject, payload,
                Guid.CreateVersion7()),
            cancellationToken);
    }
}
