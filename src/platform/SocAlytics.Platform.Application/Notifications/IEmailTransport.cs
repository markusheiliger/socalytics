namespace SocAlytics.Platform.Application.Notifications;

public interface IEmailTransport
{
    /// <summary>Hands one message to the SMTP relay; throws <see cref="EmailTransportException"/> when the relay did not accept it.</summary>
    Task SendAsync(OutboundEmail email, CancellationToken cancellationToken);
}

/// <param name="MessageId">The identity of the queued message; the transport derives the Message-ID header from it.</param>
/// <param name="ToAddress">The recipient's current address at send time.</param>
public sealed record OutboundEmail(string MessageId, string ToAddress, string Subject, string TextBody, string HtmlBody);

public enum EmailFailureKind
{
    /// <summary>The attempt may be repeated later (4xx replies, connection errors, timeouts).</summary>
    Transient,

    /// <summary>The relay refused the message for good (5xx replies, unusable message).</summary>
    Permanent,
}

public static class EmailFailureCategories
{
    public const string Unavailable = "smtp-unavailable";
    public const string Timeout = "smtp-timeout";
    public const string Authentication = "smtp-authentication";
    public const string TemporaryRejection = "smtp-temporary-rejection";
    public const string PermanentRejection = "smtp-permanent-rejection";
    public const string InvalidMessage = "invalid-message";
}

/// <summary>Carries only a category and the SMTP status code, never an address, body, or token.</summary>
public sealed class EmailTransportException(EmailFailureKind kind, string failureCategory, int? smtpStatusCode = null)
    : Exception(failureCategory)
{
    public EmailFailureKind Kind { get; } = kind;

    public string FailureCategory { get; } = failureCategory;

    public int? SmtpStatusCode { get; } = smtpStatusCode;
}
