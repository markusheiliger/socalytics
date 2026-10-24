namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

/// <summary>The limits and timings of member invitations; development values until production values are decided.</summary>
public sealed class InvitationOptions
{
    public const string SectionName = "Invitations";

    /// <summary>How long an issued invitation link stays usable.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Resends per invitation in the last hour.</summary>
    public int MaxResendsPerHour { get; set; } = 3;

    /// <summary>Token checks and acceptances per minute from one client address.</summary>
    public int TokenAttemptsPerMinute { get; set; } = 10;

    public static bool IsValid(InvitationOptions options) =>
        options.LinkLifetime > TimeSpan.Zero
        && options.MaxResendsPerHour > 0
        && options.TokenAttemptsPerMinute > 0;
}
