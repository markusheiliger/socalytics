using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed class ReconcileAgentWorkHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IOutbox outbox,
    IContractValidator validator,
    IQueueCredentialIssuer queue,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public const int BatchSize = 50;

    /// <summary>Republishes the ready notification of queued invocations that were not notified within the republish bound; returns the count.</summary>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var bound = options.CurrentValue.Monitor.RepublishBound;
        IReadOnlyList<AgentInvocationRef> due;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await invocations.ListQueuedNotifiedBeforeAsync(time.GetUtcNow() - bound, BatchSize, cancellationToken);
        }

        var republished = 0;
        foreach (var item in due)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);

            // The decision is re-checked under the lock, so a concurrent notification or claim makes this a no-op.
            var invocation = await invocations.LockAsync(item.InvocationId, cancellationToken);
            var now = time.GetUtcNow();
            if (invocation is not { State: AgentInvocationState.Queued }
                || (invocation.LastNotifiedAt ?? invocation.SubmittedAt) >= now - bound)
            {
                continue;
            }

            await AgentReadyNotification.EnqueueAsync(outbox, validator, queue, invocation, invocation.ReadyOccurrence, cancellationToken);
            await invocations.SetLastNotifiedAsync(invocation.Id, now, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            republished++;
        }

        return republished;
    }
}
