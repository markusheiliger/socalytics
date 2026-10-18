using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.Notifications;

public interface IEmailOutbox
{
    /// <summary>
    /// Queues a message for the recipient in the active unit of work, bound to the recipient's current address.
    /// Returns null when the recipient has no address or the type is unknown.
    /// </summary>
    Task<Guid?> QueueAsync(string notificationType, Guid accountId, Guid? confirmationId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a <c>password-recovery</c> message in the active unit of work, bound to the given address hash and recovery
    /// instead of the account's current address, which the delivery processor checks before sending.
    /// </summary>
    Task<Guid> QueuePasswordRecoveryAsync(Guid accountId, string addressHash, Guid passwordRecoveryId, CancellationToken cancellationToken);
}

internal sealed class EmailOutbox(IMemberEmailAddressStore addresses, IEmailMessageStore messages, TimeProvider timeProvider) : IEmailOutbox
{
    public async Task<Guid?> QueueAsync(string notificationType, Guid accountId, Guid? confirmationId, CancellationToken cancellationToken)
    {
        if (NotificationTypes.Find(notificationType) is null)
        {
            return null;
        }

        var stored = await addresses.FindAsync(accountId, cancellationToken);
        if (stored is null || !EmailAddress.TryCreate(stored.Address, out var address, out _))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid();
        await messages.QueueAsync(
            new NewEmailMessage(id, notificationType, accountId, address.Hash, confirmationId, now, now), cancellationToken);
        return id;
    }

    public async Task<Guid> QueuePasswordRecoveryAsync(Guid accountId, string addressHash, Guid passwordRecoveryId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid();
        await messages.QueueAsync(
            new NewEmailMessage(id, NotificationTypes.PasswordRecoveryCode, accountId, addressHash, null, now, now, passwordRecoveryId),
            cancellationToken);
        return id;
    }
}
