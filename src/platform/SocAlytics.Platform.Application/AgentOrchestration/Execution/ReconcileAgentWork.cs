using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed class ReconcileAgentWorkHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentConversationStore conversations,
    IAuditTrail audit,
    IOutbox outbox,
    IContractValidator validator,
    IQueueCredentialIssuer queue,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public const int BatchSize = 50;

    /// <summary>Reapplies tombstones, then republishes the ready notification of queued invocations that were not notified within the republish bound; returns the republished count.</summary>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        await ReapplyTombstonesAsync(cancellationToken);

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

    private async Task ReapplyTombstonesAsync(CancellationToken cancellationToken)
    {
        var purge = options.CurrentValue.Purge;

        IReadOnlyList<AgentConversationTombstoneRecord> inconsistent;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            inconsistent = await conversations.ListInconsistentTombstonesAsync(BatchSize, cancellationToken);
        }

        foreach (var tombstone in inconsistent)
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);

            // Everything is reasserted under the conversation lock; each step is idempotent.
            if (await conversations.LockByIdAsync(tombstone.ConversationId, cancellationToken) is null)
            {
                continue;
            }

            var now = time.GetUtcNow();
            await conversations.ReassertDeletedAsync(tombstone.ConversationId, cancellationToken);
            await attempts.FenceOpenForConversationAsync(tombstone.ConversationId, now, cancellationToken);
            await invocations.SuppressNonterminalAsync(tombstone.ConversationId, now, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        // Missing purge records of every tombstone are recreated; existing records are kept.
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            await conversations.EnsureMissingPurgeRecordsAsync(BatchSize, time.GetUtcNow(), cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        IReadOnlyList<AgentPurgeRecord> unresolved;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            unresolved = await conversations.ClaimUnresolvedPurgesAsync(purge.BatchSize, cancellationToken);
        }

        foreach (var item in unresolved)
        {
            try
            {
                await RetryUnresolvedAsync(item, purge.BatchSize, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The record stays unresolved and is retried at the next reconciliation.
            }
        }
    }

    private async Task RetryUnresolvedAsync(AgentPurgeRecord item, int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        var claimed = (await conversations.ClaimUnresolvedPurgesAsync(batchSize, cancellationToken)).FirstOrDefault(c =>
            c.ConversationId == item.ConversationId && string.Equals(c.Copy, item.Copy, StringComparison.Ordinal));
        var tombstone = claimed is null ? null : await conversations.FindTombstoneAsync(item.ConversationId, cancellationToken);
        if (tombstone is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var outcome = AgentPersistedNames.PurgeOutcomeNotPresent;
        if (string.Equals(item.Copy, AgentPersistedNames.PurgeCopyPrimary, StringComparison.Ordinal)
            && await conversations.PurgePrimaryAsync(item.ConversationId, now, cancellationToken) > 0)
        {
            outcome = AgentPersistedNames.PurgeOutcomePurged;
        }

        await conversations.CompletePurgeAsync(item.ConversationId, item.Copy, outcome, now, cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ConversationPurgeCompleted(item.ConversationId, tombstone.TeamId, item.ConversationId, item.Copy),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }
}
