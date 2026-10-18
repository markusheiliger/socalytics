using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.ManagerVersions;

public sealed class ManagerVersionOptions
{
    public const string SectionName = "Registry:ManagerVersion";

    public const string InvalidMessage = "Registry:ManagerVersion:Minimum must be empty or have the shape MAJOR.MINOR.PATCH.";

    // Empty means no minimum.
    public string Minimum { get; set; } = string.Empty;

    public static bool IsValid(ManagerVersionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrEmpty(options.Minimum) || ManagerVersion.TryParse(options.Minimum, out _);
    }
}
