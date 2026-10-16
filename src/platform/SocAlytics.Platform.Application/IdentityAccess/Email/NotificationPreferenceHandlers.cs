using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Email;

public sealed record SetNotificationPreferenceCommand(Guid AccountId, string? Type, bool Enabled);

public sealed record NotificationPreferenceItem(string Type, bool Enabled, bool AccountMessage);

internal static class NotificationPreferenceRules
{
    public const string NotConfigurableCode = "notification-type-not-configurable";

    public static bool Applies(NotificationType type, MemberAccessSnapshot snapshot) => type.Audience switch
    {
        NotificationAudience.ClubAdmins => snapshot.ClubRoles.Contains(ClubRole.ClubAdmin),
        _ => true,
    };
}

public sealed class ListNotificationPreferencesHandler(
    IUnitOfWork unitOfWork,
    IMemberAccountStore accounts,
    INotificationPreferenceStore preferences)
{
    public async Task<OperationResult<IReadOnlyList<NotificationPreferenceItem>>> HandleAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await accounts.GetAccessSnapshotAsync(accountId, cancellationToken);
        if (snapshot is null)
        {
            return OperationFailure.NotFound();
        }

        var stored = (await preferences.ListAsync(accountId, cancellationToken)).ToDictionary(p => p.NotificationType, p => p.Enabled);
        IReadOnlyList<NotificationPreferenceItem> items = NotificationTypes.All
            .Where(type => NotificationPreferenceRules.Applies(type, snapshot))
            .Select(type => new NotificationPreferenceItem(
                type.Code,
                type.AccountMessage || (stored.TryGetValue(type.Code, out var enabled) ? enabled : type.DefaultEnabled),
                type.AccountMessage))
            .ToList();
        return OperationResult<IReadOnlyList<NotificationPreferenceItem>>.Success(items);
    }
}

public sealed class SetNotificationPreferenceHandler(
    IUnitOfWork unitOfWork,
    IMemberAccountStore accounts,
    INotificationPreferenceStore preferences,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<NotificationPreferenceItem>> HandleAsync(SetNotificationPreferenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var type = NotificationTypes.Find(command.Type);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await accounts.GetAccessSnapshotAsync(command.AccountId, cancellationToken);
        if (snapshot is null)
        {
            return OperationFailure.NotFound();
        }

        if (type is null || type.AccountMessage || !NotificationPreferenceRules.Applies(type, snapshot))
        {
            return OperationFailure.Validation(NotificationPreferenceRules.NotConfigurableCode);
        }

        var existing = await preferences.FindAsync(command.AccountId, type.Code, cancellationToken);
        var previous = existing?.Enabled ?? type.DefaultEnabled;
        if (existing is not null || command.Enabled != type.DefaultEnabled)
        {
            // A stored row is kept in step instead of deleted, so the stored value never contradicts the answer.
            await preferences.SetAsync(command.AccountId, type.Code, command.Enabled, time.GetUtcNow(), cancellationToken);
        }

        if (previous != command.Enabled)
        {
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "identity.notification-preference-changed",
                    Action = "change-notification-preference",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("member", command.AccountId.ToString()),
                    Details = new Dictionary<string, string>
                    {
                        ["notificationType"] = type.Code,
                        ["enabled"] = command.Enabled ? "true" : "false",
                    },
                },
                cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        return new NotificationPreferenceItem(type.Code, command.Enabled, false);
    }
}
