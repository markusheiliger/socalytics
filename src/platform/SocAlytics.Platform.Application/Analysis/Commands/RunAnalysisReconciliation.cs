using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Queries;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed class RunAnalysisReconciliationHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, ReconcileAnalysisPublicationHandler reconcile,
    IAnalysisOperationalQueries queries, IAuditTrail audit)
{
    public async Task<OperationResult<ReconciliationResult>> HandleAsync(CancellationToken cancellationToken)
    {
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            if (!(await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken)).IsGranted)
            {
                return OperationFailure.Forbidden();
            }
        }

        // The reconciliation owns its units of work, so the audit event is recorded in the one that follows it.
        var result = await reconcile.HandleAsync(cancellationToken);

        await using var auditScope = await unitOfWork.BeginAsync(cancellationToken);
        var clubId = await queries.GetClubIdAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "analysis.publication-reconciled",
                Action = "reconcile",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("club", clubId?.ToString()),
                Details = new Dictionary<string, string>
                {
                    ["readyJobsRepublished"] = result.ReadyJobsRepublished.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["notificationsRepublished"] = result.NotificationsRepublished.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["runsSuperseded"] = result.RunsSuperseded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);
        await auditScope.CommitAsync(cancellationToken);
        return result;
    }
}
