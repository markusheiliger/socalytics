using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.IdentityAccess.Email;

namespace SocAlytics.Platform.Application.Notifications;

/// <summary>Queues the coalesced <c>registration-pending</c> message for every eligible Club Admin.</summary>
public sealed class RegistrationPendingNotifier(
    INotificationRecipientStore recipients,
    IEmailOutbox outbox,
    IOptions<NotificationOptions> options,
    TimeProvider time)
{
    /// <summary>Must run in the unit of work that creates the pending request.</summary>
    public async Task NotifyAsync(CancellationToken cancellationToken)
    {
        var type = NotificationTypes.RegistrationPending;
        var admins = await recipients.ListVerifiedClubAdminsAsync(type.Code, type.DefaultEnabled, cancellationToken);
        foreach (var accountId in admins)
        {
            // Serializes concurrent submissions per recipient so the window check cannot be raced.
            await recipients.LockRecipientAsync(type.Code, accountId, cancellationToken);
            var since = time.GetUtcNow() - options.Value.CoalescingWindow;
            if (await recipients.HasPendingOrSentSinceAsync(type.Code, accountId, since, cancellationToken))
            {
                continue;
            }

            await outbox.QueueAsync(type.Code, accountId, null, cancellationToken);
        }
    }
}
