using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public enum ClaimedKeyProtection
{
    Tpm,
    SecureEnclave,
    Pkcs11Token,
    Software,
}

public static class ClaimedKeyProtectionExtensions
{
    public static string ToApiName(this ClaimedKeyProtection value) => value switch
    {
        ClaimedKeyProtection.Tpm => "tpm",
        ClaimedKeyProtection.SecureEnclave => "secure-enclave",
        ClaimedKeyProtection.Pkcs11Token => "pkcs11-token",
        ClaimedKeyProtection.Software => "software",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParse(string? name, out ClaimedKeyProtection value)
    {
        foreach (var candidate in Enum.GetValues<ClaimedKeyProtection>())
        {
            if (string.Equals(candidate.ToApiName(), name, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public enum ClaimedApplicationProfile
{
    Tray,
    HeadlessServer,
}

public static class ClaimedApplicationProfileExtensions
{
    public static string ToApiName(this ClaimedApplicationProfile value) => value switch
    {
        ClaimedApplicationProfile.Tray => "tray",
        ClaimedApplicationProfile.HeadlessServer => "headless-server",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParse(string? name, out ClaimedApplicationProfile value)
    {
        foreach (var candidate in Enum.GetValues<ClaimedApplicationProfile>())
        {
            if (string.Equals(candidate.ToApiName(), name, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed class DeviceMetadata
{
    private static readonly string[] OperatingSystems = ["windows", "linux", "macos"];
    private static readonly string[] Architectures = ["x64", "arm64"];

    private DeviceMetadata(
        string os, string osVersion, string architecture, string managerVersion,
        ClaimedKeyProtection claimedKeyProtection, ClaimedApplicationProfile? claimedApplicationProfile, string? hostLabel)
    {
        Os = os;
        OsVersion = osVersion;
        Architecture = architecture;
        ManagerVersion = managerVersion;
        ClaimedKeyProtection = claimedKeyProtection;
        ClaimedApplicationProfile = claimedApplicationProfile;
        HostLabel = hostLabel;
    }

    public string Os { get; }

    public string OsVersion { get; }

    public string Architecture { get; }

    public string ManagerVersion { get; }

    // Claimed, not attested; read only by the FR-041 software-key refusal, never by a transition.
    public ClaimedKeyProtection ClaimedKeyProtection { get; }

    // Claimed, not attested; null means not reported (a Manager built before the profile claim).
    public ClaimedApplicationProfile? ClaimedApplicationProfile { get; }

    public string? HostLabel { get; }

    public static bool TryCreate(
        string? os, string? osVersion, string? architecture, string? managerVersion,
        string? claimedKeyProtection, string? claimedApplicationProfile, string? hostLabel,
        out DeviceMetadata? metadata, out IReadOnlyList<FieldViolation> violations)
    {
        var errors = new List<FieldViolation>();
        if (os is null || !OperatingSystems.Contains(os))
        {
            errors.Add(new("os", "Must be windows, linux, or macos."));
        }

        if (string.IsNullOrEmpty(osVersion) || osVersion.Length > 64)
        {
            errors.Add(new("osVersion", "Required, at most 64 characters."));
        }

        if (architecture is null || !Architectures.Contains(architecture))
        {
            errors.Add(new("architecture", "Must be x64 or arm64."));
        }

        if (string.IsNullOrEmpty(managerVersion) || managerVersion.Length > 32)
        {
            errors.Add(new("managerVersion", "Required, at most 32 characters."));
        }

        if (!ClaimedKeyProtectionExtensions.TryParse(claimedKeyProtection, out var protection))
        {
            errors.Add(new("claimedKeyProtection", "Must be tpm, secure-enclave, pkcs11-token, or software."));
        }

        ClaimedApplicationProfile? profile = null;
        if (claimedApplicationProfile is not null)
        {
            if (!ClaimedApplicationProfileExtensions.TryParse(claimedApplicationProfile, out var parsedProfile))
            {
                errors.Add(new("claimedApplicationProfile", "Must be tray or headless-server."));
            }
            else
            {
                profile = parsedProfile;
                if (parsedProfile == Registry.ClaimedApplicationProfile.HeadlessServer && os is not null && OperatingSystems.Contains(os) && os != "linux")
                {
                    errors.Add(new("claimedApplicationProfile", "The headless server profile is available only on Linux."));
                }
            }
        }

        if (hostLabel is not null && (hostLabel.Length == 0 || hostLabel.Length > 64))
        {
            errors.Add(new("hostLabel", "Optional, 1 to 64 characters."));
        }
        else if (hostLabel is not null && hostLabel.Any(char.IsControl))
        {
            errors.Add(new("hostLabel", "Must not contain control characters."));
        }

        violations = errors;
        metadata = errors.Count == 0
            ? new DeviceMetadata(os!, osVersion!, architecture!, managerVersion!, protection, profile, hostLabel)
            : null;
        return errors.Count == 0;
    }
}
