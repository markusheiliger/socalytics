using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SocAlytics.Platform.Infrastructure.Email;

internal sealed class EmailOptions
{
    public const string SectionName = "Email";

    public SmtpOptions Smtp { get; set; } = new();

    public string SenderAddress { get; set; } = string.Empty;

    public string SenderName { get; set; } = string.Empty;

    public ConfirmationOptions Confirmation { get; set; } = new();

    public DeliveryOptions Delivery { get; set; } = new();

    internal sealed class SmtpOptions
    {
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 587;

        public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

        public string? UserName { get; set; }

        public string? Password { get; set; }
    }

    internal sealed class ConfirmationOptions
    {
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(1);

        public int MaxPerHour { get; set; } = 3;

        public int AttemptsPerMinute { get; set; } = 10;
    }

    internal sealed class DeliveryOptions
    {
        public int MaxAttempts { get; set; } = 8;

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    }
}

internal enum SmtpSecurity
{
    SslOnConnect,
    StartTls,

    /// <summary>Development only: upgrades to TLS when the relay offers it and otherwise stays plain.</summary>
    StartTlsWhenAvailable,
}

internal sealed class EmailOptionsValidator(IHostEnvironment environment) : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            failures.Add("Email:Smtp:Host must be configured.");
        }

        if (options.Smtp.Port is < 1 or > 65535)
        {
            failures.Add("Email:Smtp:Port must be between 1 and 65535.");
        }

        if (!Enum.IsDefined(options.Smtp.Security))
        {
            failures.Add("Email:Smtp:Security must be SslOnConnect, StartTls, or StartTlsWhenAvailable.");
        }
        else if (options.Smtp.Security == SmtpSecurity.StartTlsWhenAvailable && !environment.IsDevelopment())
        {
            failures.Add("Email:Smtp:Security must be SslOnConnect or StartTls outside the Development environment.");
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.UserName) != string.IsNullOrWhiteSpace(options.Smtp.Password))
        {
            failures.Add("Email:Smtp:UserName and Email:Smtp:Password must be configured together.");
        }

        if (!MailboxAddress.TryParse(options.SenderAddress ?? string.Empty, out var sender) || !sender.Address.Contains('@'))
        {
            failures.Add("Email:SenderAddress must be a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(options.SenderName))
        {
            failures.Add("Email:SenderName must be configured.");
        }

        Positive(failures, options.Confirmation.Lifetime, "Email:Confirmation:Lifetime");
        Positive(failures, options.Confirmation.MaxPerHour, "Email:Confirmation:MaxPerHour");
        Positive(failures, options.Confirmation.AttemptsPerMinute, "Email:Confirmation:AttemptsPerMinute");
        Positive(failures, options.Delivery.MaxAttempts, "Email:Delivery:MaxAttempts");
        Positive(failures, options.Delivery.PollInterval, "Email:Delivery:PollInterval");
        Positive(failures, options.Delivery.LeaseDuration, "Email:Delivery:LeaseDuration");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Positive(List<string> failures, int value, string setting)
    {
        if (value <= 0)
        {
            failures.Add($"{setting} must be greater than zero.");
        }
    }

    private static void Positive(List<string> failures, TimeSpan value, string setting)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{setting} must be greater than zero.");
        }
    }
}
