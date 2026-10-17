namespace SocAlytics.Platform.Application.Notifications;

public enum NotificationAudience
{
    /// <summary>Only the owner of the address the message is about.</summary>
    AddressOwner,

    /// <summary>Active members holding the Club Admin role.</summary>
    ClubAdmins,
}

/// <param name="Code">The stable code stored in <c>email_messages.notification_type</c>.</param>
/// <param name="AccountMessage">Account messages are always sent and have no preference.</param>
/// <param name="DefaultEnabled">The preference value while the member has not changed it.</param>
/// <param name="LinkPath">The path below the Web UI base address the message links to.</param>
public sealed record NotificationType(string Code, NotificationAudience Audience, bool AccountMessage, bool DefaultEnabled, string LinkPath);

public static class NotificationTypes
{
    public const string EmailConfirmationCode = "email-confirmation";
    public const string RegistrationPendingCode = "registration-pending";

    public static NotificationType EmailConfirmation { get; } = new(
        EmailConfirmationCode, NotificationAudience.AddressOwner, AccountMessage: true, DefaultEnabled: true, "/email/confirm");

    public static NotificationType RegistrationPending { get; } = new(
        RegistrationPendingCode, NotificationAudience.ClubAdmins, AccountMessage: false, DefaultEnabled: true, "/analyst-managers");

    public static IReadOnlyList<NotificationType> All { get; } = [EmailConfirmation, RegistrationPending];

    public static NotificationType? Find(string? code) => All.FirstOrDefault(t => t.Code == code);
}
