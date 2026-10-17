using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using SocAlytics.Platform.Application.Notifications;

namespace SocAlytics.Platform.Infrastructure.Email;

internal sealed class MailKitEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        var settings = options.Value;

        MimeMessage message;
        try
        {
            message = Build(email, settings);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new EmailTransportException(EmailFailureKind.Permanent, EmailFailureCategories.InvalidMessage);
        }

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, ToSocketOptions(settings.Smtp.Security), cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.Smtp.UserName))
            {
                await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Map(ex);
        }

        // The relay has accepted the message; a failing goodbye must not turn into a retry and a duplicate.
        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    internal static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        _ => throw new ArgumentOutOfRangeException(nameof(security)),
    };

    internal static EmailTransportException Map(Exception exception) => exception switch
    {
        SmtpCommandException command when (int)command.StatusCode >= 500 =>
            new(EmailFailureKind.Permanent, EmailFailureCategories.PermanentRejection, (int)command.StatusCode),
        SmtpCommandException command =>
            new(EmailFailureKind.Transient, EmailFailureCategories.TemporaryRejection, (int)command.StatusCode),
        AuthenticationException => new(EmailFailureKind.Transient, EmailFailureCategories.Authentication),
        TimeoutException or OperationCanceledException => new(EmailFailureKind.Transient, EmailFailureCategories.Timeout),
        _ => new(EmailFailureKind.Transient, EmailFailureCategories.Unavailable),
    };

    private static MimeMessage Build(OutboundEmail email, EmailOptions settings)
    {
        var sender = MailboxAddress.Parse(settings.SenderAddress);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.SenderName, sender.Address));
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;
        message.MessageId = $"{email.MessageId}@{sender.Domain}";
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();
        return message;
    }
}
