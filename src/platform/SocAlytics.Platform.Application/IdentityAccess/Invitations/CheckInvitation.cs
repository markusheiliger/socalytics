using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record CheckInvitationCommand(string? Token);

public sealed record InvitationCheckResult(string ClubName, string AccountName);

/// <summary>The one usability check shared by the check and the acceptance; every failure is the same neutral validation failure.</summary>
internal sealed class InvitationTokenValidator(IMemberInvitationStore invitations, TimeProvider time)
{
    public const string InvalidCode = InvitationAuditEvents.ReasonInvitationInvalid;

    public static OperationFailure Invalid() => OperationFailure.Validation(InvalidCode);

    /// <summary>Must run inside a unit of work; locks the link and its invitation and changes nothing.</summary>
    public async Task<StoredInvitationLinkWithInvitation?> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return null;
        }

        var found = await invitations.FindLinkByTokenHashForUpdateAsync(ConfirmationTokens.Hash(token), cancellationToken);
        if (found is null
            || found.Link.State != "issued"
            || found.Link.ExpiresAt is not { } expiresAt
            || expiresAt <= time.GetUtcNow()
            || found.Invitation.Invitation.State != InvitationState.Pending)
        {
            return null;
        }

        return found;
    }
}

public sealed class CheckInvitationHandler(
    IUnitOfWork unitOfWork,
    IMemberInvitationStore invitations,
    IClubHierarchyStore clubs,
    TimeProvider time)
{
    public async Task<OperationResult<InvitationCheckResult>> HandleAsync(CheckInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var found = await new InvitationTokenValidator(invitations, time).ValidateAsync(command.Token, cancellationToken);
        var club = found is null ? null : await clubs.GetClubAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return found is null || club is null
            ? InvitationTokenValidator.Invalid()
            : new InvitationCheckResult(club.DisplayName.Value, found.Invitation.Invitation.AccountName.Value);
    }
}
