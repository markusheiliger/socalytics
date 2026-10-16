namespace SocAlytics.Platform.Application.IdentityAccess.Email;

/// <summary>The limits of the confirmation operations; the section is shared with the delivery settings in Infrastructure.</summary>
public sealed class EmailConfirmationOptions
{
    public const string SectionName = "Email:Confirmation";

    /// <summary>Confirmation messages per account in the last hour (resend limit).</summary>
    public int MaxPerHour { get; set; } = 3;

    /// <summary>Confirmation attempts per minute from one client address.</summary>
    public int AttemptsPerMinute { get; set; } = 10;

    public static bool IsValid(EmailConfirmationOptions options) => options.MaxPerHour > 0 && options.AttemptsPerMinute > 0;
}
