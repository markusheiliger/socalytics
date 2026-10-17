namespace SocAlytics.Platform.Application.Notifications;

public interface IEmailRenderer
{
    /// <summary>Renders the message of a notification type; throws <see cref="ArgumentException"/> for an unknown type and <see cref="InvalidOperationException"/> when a value the type needs is missing.</summary>
    RenderedEmail Render(string notificationType, EmailRenderModel model);
}

/// <param name="ClubName">The club's display name (confirmation message).</param>
/// <param name="Link">The only link of the message.</param>
/// <param name="LinkLifetime">How long the link stays valid (confirmation message).</param>
public sealed record EmailRenderModel(string? ClubName, string Link, TimeSpan? LinkLifetime = null);

public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);
