using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed class PurgeAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAuditTrail audit,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public const string PurgeFailedReason = "purge-failed";

    /// <summary>Purges the due pending purge records, one unit of work per record; returns the number completed.</summary>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var purge = options.CurrentValue.Purge;

        IReadOnlyList<AgentPurgeRecord> due;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await conversations.ClaimDuePurgesAsync(time.GetUtcNow(), purge.BatchSize, cancellationToken);
        }

        var completed = 0;
        foreach (var item in due)
        {
            try
            {
                if (await TryPurgeAsync(item, purge, cancellationToken))
                {
                    completed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The failed unit of work is rolled back; the failure is counted in a unit of its own.
                await RecordFailureAsync(item, purge, cancellationToken);
            }
        }

        return completed;
    }

    private async Task<bool> TryPurgeAsync(
        AgentPurgeRecord item, AgentOrchestrationOptions.PurgeOptions purge, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // The record is claimed again under its lock, so a concurrent worker or a completed purge makes this a no-op.
        var now = time.GetUtcNow();
        var claimed = await ClaimAsync(item, now, purge, cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        var tombstone = await conversations.FindTombstoneAsync(item.ConversationId, cancellationToken);
        if (tombstone is null)
        {
            return false;
        }

        var outcome = string.Equals(item.Copy, AgentPersistedNames.PurgeCopyPrimary, StringComparison.Ordinal)
            ? await PurgePrimaryAsync(item.ConversationId, now, cancellationToken)
            : AgentPersistedNames.PurgeOutcomeNotPresent;

        await conversations.CompletePurgeAsync(item.ConversationId, item.Copy, outcome, now, cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ConversationPurgeCompleted(item.ConversationId, tombstone.TeamId, item.ConversationId, item.Copy),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<string> PurgePrimaryAsync(Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var removed = await conversations.PurgePrimaryAsync(conversationId, now, cancellationToken);
        return removed > 0 ? AgentPersistedNames.PurgeOutcomePurged : AgentPersistedNames.PurgeOutcomeNotPresent;
    }

    private async Task RecordFailureAsync(
        AgentPurgeRecord item, AgentOrchestrationOptions.PurgeOptions purge, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var now = time.GetUtcNow();
        var claimed = await ClaimAsync(item, now, purge, cancellationToken);
        if (claimed is null)
        {
            return;
        }

        if (claimed.Attempts + 1 >= purge.MaxAttempts)
        {
            await conversations.MarkPurgeUnresolvedAsync(item.ConversationId, item.Copy, now, cancellationToken);
            var tombstone = await conversations.FindTombstoneAsync(item.ConversationId, cancellationToken);
            if (tombstone is not null)
            {
                await audit.RecordAsync(
                    AgentOrchestrationAuditEvents.ConversationPurgeUnresolved(
                        item.ConversationId, tombstone.TeamId, item.ConversationId, item.Copy, PurgeFailedReason),
                    cancellationToken);
            }
        }
        else
        {
            await conversations.RecordPurgeFailureAsync(item.ConversationId, item.Copy, now, now + purge.RetryDelay, cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
    }

    private async Task<AgentPurgeRecord?> ClaimAsync(
        AgentPurgeRecord item, DateTimeOffset now, AgentOrchestrationOptions.PurgeOptions purge, CancellationToken cancellationToken)
    {
        var claimed = await conversations.ClaimDuePurgesAsync(now, purge.BatchSize, cancellationToken);
        return claimed.FirstOrDefault(c =>
            c.ConversationId == item.ConversationId && string.Equals(c.Copy, item.Copy, StringComparison.Ordinal));
    }
}
