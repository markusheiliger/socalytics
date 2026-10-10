using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Expiry;

public sealed record ExpiryRunResult(int Expired, int PurgedReplayRows);

public sealed class ExpireDueRegistrationsHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore store,
    IProofReplayStore replay,
    IManagerAccessTokenStore tokens,
    IAuditTrail audit,
    TimeProvider time)
{
    private const int BatchSize = 100;

    public async Task<ExpiryRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expired = 0;
        while (true)
        {
            IReadOnlyList<AnalystManagerRegistration> due;
            await using (var read = await unitOfWork.BeginAsync(cancellationToken))
            {
                due = await store.ListDueForExpiryAsync(now, BatchSize, cancellationToken);
            }

            var progressed = 0;
            foreach (var registration in due)
            {
                if (await ExpireOneAsync(registration, now, cancellationToken))
                {
                    progressed++;
                }
            }

            expired += progressed;
            if (progressed == 0 || due.Count < BatchSize)
            {
                break;
            }
        }

        int purged;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            purged = await replay.PurgeExpiredAsync(now, cancellationToken);
            await tokens.PurgeExpiredAsync(now, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new ExpiryRunResult(expired, purged);
    }

    private async Task<bool> ExpireOneAsync(AnalystManagerRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = registration.State;
        var reason = from switch
        {
            RegistrationState.Pairing => AnalystManagerRegistration.PairingWindowElapsed,
            RegistrationState.PendingApproval => AnalystManagerRegistration.ApprovalWindowElapsed,
            _ => AnalystManagerRegistration.ActivationWindowElapsed,
        };

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!registration.Expire(reason, now).Succeeded
            || (await store.UpdateAsync(registration, from, true, cancellationToken)).Outcome != VersionedWriteOutcome.Applied)
        {
            return false;
        }

        await audit.RecordAsync(RegistryAuditEvents.Expired(registration.Id, reason, new Dictionary<string, string>
        {
            ["fromState"] = from.ToString(),
            ["toState"] = nameof(RegistrationState.Expired),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
