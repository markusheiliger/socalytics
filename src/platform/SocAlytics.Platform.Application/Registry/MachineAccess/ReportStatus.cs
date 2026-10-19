using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.ManagerVersions;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record ReportStatusCommand(
    string? OperatingState, DateTimeOffset? PausedUntil, string? RuntimeUnavailableReason,
    int? ActiveAnalysts, int? MaxAnalysts, string? ManagerVersion);

public sealed record ReportStatusResult(
    string ManagerId, string StampId, string State, string ClubName, string KeyThumbprint, DateTimeOffset ActivatedAt,
    int StatusCheckIntervalSeconds, bool ReportAccepted, IReadOnlyList<FieldViolation> ReportViolations,
    string? MinimumManagerVersion);

public sealed class ReportStatusHandler(
    IUnitOfWork unitOfWork, IRegistrationStore registrations, IClubHierarchyStore clubs, IManagerStatusStore statuses,
    TimeProvider time, IOptions<StatusReportingOptions> statusReporting, MinimumManagerVersionProvider minimumVersion)
{
    public async Task<OperationResult<ReportStatusResult>> HandleAsync(
        Guid registrationId, ReportStatusCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(registrationId, cancellationToken);
        var club = await clubs.GetClubAsync(cancellationToken);
        if (registration?.ManagerId is null || registration.ActivatedAt is null || club is null
            || registration.State is not (RegistrationState.Active or RegistrationState.Unregistering))
        {
            return OperationFailure.Unauthenticated();
        }

        var intervalSeconds = statusReporting.Value.IntervalSeconds;
        var accepted = ManagerStatusReport.TryCreate(
            command.OperatingState, command.PausedUntil, command.RuntimeUnavailableReason,
            command.ActiveAnalysts, command.MaxAnalysts, command.ManagerVersion, out var report, out var violations);
        if (accepted)
        {
            await statuses.UpsertAsync(registrationId, report!, time.GetUtcNow(), intervalSeconds, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new ReportStatusResult(
            registration.ManagerId.Value.ToString(), registration.StampId,
            registration.State == RegistrationState.Active ? "active" : "unregistering",
            club.DisplayName.Value, registration.KeyThumbprint, registration.ActivatedAt.Value,
            intervalSeconds, accepted,
            violations.Select(v => new FieldViolation(v.Field, "invalid")).ToList(),
            minimumVersion.Current?.Value);
    }
}
