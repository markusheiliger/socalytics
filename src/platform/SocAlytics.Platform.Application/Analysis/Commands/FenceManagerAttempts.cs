using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Application.Analysis.Commands;

/// <summary>Fences every active attempt of a revoked or unregistered Manager; runs inside the caller's unit of work.</summary>
public sealed class FenceManagerAttempts(IExecutionAttemptStore attempts, CloseAttemptAsStale closeAsStale)
{
    public const string RevokedReason = "manager-revoked";

    public async Task<int> HandleAsync(Guid managerId, CancellationToken cancellationToken)
    {
        var active = await attempts.ListActiveForManagerAsync(managerId, cancellationToken);
        var fenced = 0;
        foreach (var item in active)
        {
            if (await closeAsStale.HandleAsync(item, RevokedReason, requireExpired: false, cancellationToken))
            {
                fenced++;
            }
        }

        return fenced;
    }
}
