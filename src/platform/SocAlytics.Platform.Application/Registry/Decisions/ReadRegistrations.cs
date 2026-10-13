using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

public sealed record FleetEntry(
    AnalystManagerRegistration Registration, StoredManagerStatus? Status, ManagerConnectivity Connectivity, bool IncludesOperations = true)
{
    public static FleetEntry Submitted(AnalystManagerRegistration registration) =>
        new(registration, null, ManagerConnectivity.NotApplicable, false);
}

public sealed record FleetPage(IReadOnlyList<FleetEntry> Items, string? ContinuationToken);

internal static class FleetEntries
{
    public static async Task<IReadOnlyList<FleetEntry>> BuildAsync(
        IReadOnlyList<AnalystManagerRegistration> registrations, IManagerStatusStore statuses,
        TimeProvider time, StatusReportingOptions options, CancellationToken cancellationToken)
    {
        var reporting = registrations
            .Where(r => r.State is RegistrationState.Active or RegistrationState.Unregistering)
            .Select(r => r.Id)
            .ToArray();
        var stored = reporting.Length == 0
            ? new Dictionary<Guid, StoredManagerStatus>()
            : await statuses.FindManyAsync(reporting, cancellationToken);
        var now = time.GetUtcNow();
        return [.. registrations.Select(r =>
        {
            var status = stored.GetValueOrDefault(r.Id);
            return new FleetEntry(r, status, Connectivity.Derive(r.State, status, now, options.MissedIntervalsBeforeOffline));
        })];
    }
}

internal sealed record FleetViewer(Guid? SubmittedBy)
{
    // Club Admins see the whole fleet; Registrars only the requests they submitted; others nothing.
    public static async Task<FleetViewer?> ResolveAsync(
        IAccessAuthorizer authorizer, IRequestContext context, AuditResource resource, CancellationToken cancellationToken)
    {
        if ((await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken)).IsGranted)
        {
            return new FleetViewer((Guid?)null);
        }

        if (context.MemberAccountId is { } member
            && (await authorizer.AuthorizeClubAsync(ClubPermission.Register, resource, cancellationToken)).IsGranted)
        {
            return new FleetViewer(member);
        }

        return null;
    }
}

public sealed class GetRegistrationHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store, IManagerStatusStore statuses,
    IRequestContext context, TimeProvider time, IOptions<StatusReportingOptions> statusReporting)
{
    public async Task<OperationResult<FleetEntry>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource(RegistryAuditEvents.ResourceType, registrationId.ToString());
        var viewer = await FleetViewer.ResolveAsync(authorizer, context, resource, cancellationToken);
        if (viewer is null)
        {
            return OperationFailure.Forbidden();
        }

        var registration = await store.FindByIdAsync(registrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.NotFound();
        }

        if (viewer.SubmittedBy is { } submitter)
        {
            return registration.SubmittedBy == submitter ? FleetEntry.Submitted(registration) : OperationFailure.NotFound();
        }

        var entries = await FleetEntries.BuildAsync([registration], statuses, time, statusReporting.Value, cancellationToken);
        return entries[0];
    }
}

public sealed class ListRegistrationsHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store, IManagerStatusStore statuses,
    IRequestContext context, TimeProvider time, IOptions<StatusReportingOptions> statusReporting)
{
    public const int PageSize = 50;

    public async Task<OperationResult<FleetPage>> HandleAsync(
        string? state, string? continuationToken, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var viewer = await FleetViewer.ResolveAsync(
            authorizer, context, new AuditResource(RegistryAuditEvents.ResourceType, null), cancellationToken);
        if (viewer is null)
        {
            return OperationFailure.Forbidden();
        }

        RegistrationState? filter = null;
        if (state is not null)
        {
            if (!RegistrationStateExtensions.TryParseApiName(state, out var parsed))
            {
                return OperationFailure.Validation(new FieldViolation("state", "invalid-value"));
            }

            filter = parsed;
        }

        try
        {
            var page = await store.ListAsync(filter, viewer.SubmittedBy, PageSize, continuationToken, cancellationToken);
            if (viewer.SubmittedBy is not null)
            {
                return new FleetPage([.. page.Items.Select(FleetEntry.Submitted)], page.ContinuationToken);
            }

            var entries = await FleetEntries.BuildAsync(page.Items, statuses, time, statusReporting.Value, cancellationToken);
            return new FleetPage(entries, page.ContinuationToken);
        }
        catch (ArgumentException)
        {
            return OperationFailure.Validation(new FieldViolation("continuationToken", "invalid"));
        }
    }
}
