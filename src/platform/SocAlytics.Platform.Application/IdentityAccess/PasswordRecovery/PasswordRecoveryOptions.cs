namespace SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

/// <summary>The limits and timings of password recovery; development values until production values are decided.</summary>
public sealed class PasswordRecoveryOptions
{
    public const string SectionName = "PasswordRecovery";

    /// <summary>How long an issued recovery link stays usable.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Recovery messages per account in the last hour.</summary>
    public int MaxMessagesPerHour { get; set; } = 3;

    /// <summary>Recovery requests per minute from one client address.</summary>
    public int RequestsPerMinute { get; set; } = 5;

    /// <summary>Token checks and completions per minute from one client address.</summary>
    public int TokenAttemptsPerMinute { get; set; } = 10;

    /// <summary>The earliest time after receipt at which a recovery request is answered.</summary>
    public TimeSpan MinimumResponseDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    public static bool IsValid(PasswordRecoveryOptions options) =>
        options.LinkLifetime > TimeSpan.Zero
        && options.MaxMessagesPerHour > 0
        && options.RequestsPerMinute > 0
        && options.TokenAttemptsPerMinute > 0
        && options.MinimumResponseDuration > TimeSpan.Zero;
}
