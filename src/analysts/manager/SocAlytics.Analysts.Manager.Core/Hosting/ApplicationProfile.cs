namespace SocAlytics.Analysts.Manager.Hosting;

/// <summary>The executable a host operator installed; fixed when the host is built, never configuration.</summary>
public enum ApplicationProfile
{
    Tray,
    HeadlessServer,
}

public static class ApplicationProfileExtensions
{
    public static string ToApiName(this ApplicationProfile profile) => profile switch
    {
        ApplicationProfile.Tray => "tray",
        ApplicationProfile.HeadlessServer => "headless-server",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };
}
