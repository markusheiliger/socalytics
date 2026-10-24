using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Application.IdentityAccess.Invitations;
using SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Infrastructure.Email;

/// <summary>Claims due messages, re-checks eligibility, issues confirmation and recovery tokens, sends, and records the outcome. Logs message id, type, and state only, never a token or an address.</summary>
internal sealed class EmailDeliveryProcessor(
    IUnitOfWork unitOfWork,
    IEmailMessageStore messages,
    IMemberEmailAddressStore addresses,
    IEmailConfirmationStore confirmations,
    IPasswordRecoveryStore recoveries,
    IMemberInvitationStore invitations,
    IAuditTrail audit,
    INotificationPreferenceStore preferences,
    IMemberAccountStore accounts,
    IClubHierarchyStore clubs,
    IEmailTransport transport,
    IEmailRenderer renderer,
    IOptions<EmailOptions> emailOptions,
    IOptions<NotificationOptions> notificationOptions,
    IOptions<PasswordRecoveryOptions> recoveryOptions,
    IOptions<InvitationOptions> invitationOptions,
    TimeProvider timeProvider,
    ILogger<EmailDeliveryProcessor> logger)
{
    public const int BatchSize = 5;

    private const string PasswordRecoveryIssuedEventType = "password-recovery.issued";
    private const string PendingState = "pending";
    private const string IssuedState = "issued";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private sealed record Prepared(StoredEmailMessage Message, OutboundEmail Email, DateTimeOffset LeaseUntil);

    /// <returns>The number of messages claimed.</returns>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var settings = emailOptions.Value;
        var prepared = new List<Prepared>();
        int claimedCount;

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            var leaseUntil = now + settings.Delivery.LeaseDuration;
            var claimed = await messages.ClaimDueAsync(now, leaseUntil, BatchSize, cancellationToken);
            claimedCount = claimed.Count;
            foreach (var message in claimed)
            {
                var email = await PrepareAsync(message, now, settings, cancellationToken);
                if (email is not null)
                {
                    prepared.Add(new Prepared(message, email, leaseUntil));
                }
            }

            await scope.CommitAsync(cancellationToken);
        }

        foreach (var item in prepared)
        {
            // A lease that ran out while earlier messages were sent belongs to the next poll.
            if (timeProvider.GetUtcNow() >= item.LeaseUntil)
            {
                continue;
            }

            await SendAsync(item.Message, item.Email, settings, cancellationToken);
        }

        return claimedCount;
    }

    private async Task<OutboundEmail?> PrepareAsync(StoredEmailMessage message, DateTimeOffset now, EmailOptions settings, CancellationToken cancellationToken)
    {
        if (message.NotificationType == NotificationTypes.MemberInvitationCode)
        {
            return await PrepareInvitationAsync(message, now, cancellationToken);
        }

        // Only member invitations are addressed without an account.
        if (message.AccountId is not { } accountId)
        {
            await DiscardAsync(message, cancellationToken);
            return null;
        }

        var type = NotificationTypes.Find(message.NotificationType);
        var snapshot = await accounts.GetAccessSnapshotAsync(accountId, cancellationToken);
        var stored = await addresses.FindAsync(accountId, cancellationToken);

        var eligible = type is not null
            && snapshot is { Status: MembershipStatus.Active }
            && stored is not null
            && EmailAddress.TryCreate(stored.Address, out var address, out _)
            && address.Hash == message.AddressHash
            && (type.Code == NotificationTypes.EmailConfirmationCode || stored.State == EmailAddressStates.Verified);

        if (eligible && !type!.AccountMessage)
        {
            eligible = type.Audience switch
            {
                NotificationAudience.ClubAdmins => snapshot!.ClubRoles.Contains(ClubRole.ClubAdmin),
                _ => false,
            };
            if (eligible)
            {
                var preference = await preferences.FindAsync(accountId, type.Code, cancellationToken);
                eligible = preference?.Enabled ?? type.DefaultEnabled;
            }
        }

        if (!eligible)
        {
            if (message.PasswordRecoveryId is { } staleRecoveryId)
            {
                await recoveries.MarkInvalidatedAsync(staleRecoveryId, cancellationToken);
            }

            await DiscardAsync(message, cancellationToken);
            return null;
        }

        var link = notificationOptions.Value.LinkTo(type!.LinkPath);
        string? clubName = null;
        TimeSpan? lifetime = null;
        if (type.Code == NotificationTypes.EmailConfirmationCode)
        {
            var confirmation = message.ConfirmationId is { } id ? await confirmations.FindAsync(id, cancellationToken) : null;
            if (confirmation is not { State: EmailConfirmationStates.Pending or EmailConfirmationStates.Issued }
                || confirmation.AddressHash != message.AddressHash)
            {
                await DiscardAsync(message, cancellationToken);
                return null;
            }

            // Each attempt issues a fresh token, so a link of an earlier attempt stops working.
            var token = ConfirmationTokens.Generate();
            lifetime = settings.Confirmation.Lifetime;
            if (!await confirmations.IssueAsync(confirmation.Id, ConfirmationTokens.Hash(token), now, now + lifetime.Value, cancellationToken))
            {
                await DiscardAsync(message, cancellationToken);
                return null;
            }

            link += "#token=" + token;
            clubName = (await clubs.GetClubAsync(cancellationToken))?.DisplayName.Value;
        }

        if (type.Code == NotificationTypes.PasswordRecoveryCode)
        {
            var recovery = message.PasswordRecoveryId is { } recoveryId ? await recoveries.FindAsync(recoveryId, cancellationToken) : null;
            if (recovery is null)
            {
                await DiscardAsync(message, cancellationToken);
                return null;
            }

            if (!EmailAddress.TryCreate(stored!.Address, out var recoveryAddress, out _))
            {
                await DiscardAsync(message, cancellationToken);
                return null;
            }

            var current = recovery.AccountId == accountId
                && recovery.State is PendingState or IssuedState
                && recovery.AddressHash == message.AddressHash
                && await HasLocalPasswordAsync(accountId, recoveryAddress.Normalized, cancellationToken);
            if (!current)
            {
                if (recovery.State is PendingState or IssuedState)
                {
                    await recoveries.MarkInvalidatedAsync(recovery.Id, cancellationToken);
                }

                await DiscardAsync(message, cancellationToken);
                return null;
            }

            // Each attempt issues a fresh token, so a link of an earlier attempt stops working.
            var token = ConfirmationTokens.Generate();
            lifetime = recoveryOptions.Value.LinkLifetime;
            if (!await recoveries.IssueAsync(recovery.Id, ConfirmationTokens.Hash(token), now, now + lifetime.Value, cancellationToken))
            {
                await DiscardAsync(message, cancellationToken);
                return null;
            }

            link += "#token=" + token;
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = PasswordRecoveryIssuedEventType,
                    Action = "issue-password-recovery",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("member", accountId.ToString()),
                    Details = new Dictionary<string, string> { ["maskedAddress"] = recoveryAddress.Masked },
                    ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
                },
                cancellationToken);
        }

        try
        {
            var rendered = renderer.Render(type.Code, new EmailRenderModel(clubName, link, lifetime));
            return new OutboundEmail(message.Id.ToString("D"), stored!.Address, rendered.Subject, rendered.TextBody, rendered.HtmlBody);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await messages.MarkFailedAsync(message.Id, EmailErrorCategories.Permanent, cancellationToken);
            LogState(message, EmailMessageStates.Failed);
            return null;
        }
    }

    private async Task<OutboundEmail?> PrepareInvitationAsync(StoredEmailMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var link = message.InvitationRef is { } linkId ? await invitations.FindLinkAsync(linkId, cancellationToken) : null;
        var stored = link is { State: PendingState or IssuedState }
            ? await invitations.FindForUpdateAsync(link.InvitationId, cancellationToken)
            : null;

        if (link is null
            || stored is not { Invitation.State: InvitationState.Pending }
            || stored.AddressHash != message.AddressHash
            || !EmailAddress.TryCreate(stored.Invitation.Address, out var address, out _))
        {
            await DiscardAsync(message, cancellationToken);
            return null;
        }

        // Each attempt issues a fresh token, so a link of an earlier attempt stops working.
        var token = ConfirmationTokens.Generate();
        var lifetime = invitationOptions.Value.LinkLifetime;
        if (!await invitations.IssueLinkAsync(link.Id, ConfirmationTokens.Hash(token), now, now + lifetime, cancellationToken))
        {
            await DiscardAsync(message, cancellationToken);
            return null;
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.Issued,
                Action = "issue",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("invitation", stored.Invitation.Id.ToString()),
                Details = new Dictionary<string, string> { ["maskedAddress"] = address.Masked },
                ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
            },
            cancellationToken);

        var url = notificationOptions.Value.LinkTo(NotificationTypes.MemberInvitation.LinkPath) + "#token=" + token;
        var clubName = (await clubs.GetClubAsync(cancellationToken))?.DisplayName.Value;
        try
        {
            var rendered = renderer.Render(NotificationTypes.MemberInvitationCode, new EmailRenderModel(clubName, url, lifetime));
            return new OutboundEmail(message.Id.ToString("D"), stored.Invitation.Address, rendered.Subject, rendered.TextBody, rendered.HtmlBody);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await messages.MarkFailedAsync(message.Id, EmailErrorCategories.Permanent, cancellationToken);
            LogState(message, EmailMessageStates.Failed);
            return null;
        }
    }

    private async Task<bool> HasLocalPasswordAsync(Guid accountId, string addressNormalized, CancellationToken cancellationToken)
    {
        var candidates = await recoveries.ListCandidatesAsync(addressNormalized, cancellationToken);
        return candidates.Any(candidate => candidate.AccountId == accountId && candidate.HasPassword);
    }

    private async Task DiscardAsync(StoredEmailMessage message, CancellationToken cancellationToken)
    {
        await messages.MarkDiscardedAsync(message.Id, EmailErrorCategories.Ineligible, cancellationToken);
        LogState(message, EmailMessageStates.Discarded);
    }

    private async Task SendAsync(StoredEmailMessage message, OutboundEmail email, EmailOptions settings, CancellationToken cancellationToken)
    {
        EmailTransportException? failure = null;
        try
        {
            await transport.SendAsync(email, cancellationToken);
        }
        catch (EmailTransportException ex)
        {
            failure = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = new EmailTransportException(EmailFailureKind.Transient, EmailFailureCategories.Unavailable);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        string state;
        if (failure is null)
        {
            await messages.MarkSentAsync(message.Id, now, cancellationToken);
            state = EmailMessageStates.Sent;
        }
        else if (failure.Kind == EmailFailureKind.Permanent)
        {
            await messages.MarkFailedAsync(message.Id, EmailErrorCategories.Permanent, cancellationToken);
            state = EmailMessageStates.Failed;
        }
        else if (message.AttemptCount >= settings.Delivery.MaxAttempts)
        {
            await messages.MarkFailedAsync(message.Id, EmailErrorCategories.Transient, cancellationToken);
            state = EmailMessageStates.Failed;
        }
        else
        {
            await messages.MarkRetryAsync(message.Id, now + Backoff(message.AttemptCount), cancellationToken);
            state = EmailMessageStates.Queued;
        }

        await scope.CommitAsync(cancellationToken);
        if (failure is null)
        {
            LogState(message, state);
        }
        else
        {
            logger.LogWarning(
                "Email message {MessageId} ({NotificationType}) is {State} after attempt {Attempt}: {Category}",
                message.Id, message.NotificationType, state, message.AttemptCount, failure.FailureCategory);
        }
    }

    // 1, 2, 4 ... minutes, capped at one hour.
    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, Math.Clamp(attempt - 1, 0, 30))));

    private void LogState(StoredEmailMessage message, string state) =>
        logger.LogInformation("Email message {MessageId} ({NotificationType}) is {State}", message.Id, message.NotificationType, state);
}
