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

public sealed class DeviceMetadata
{
    private static readonly string[] OperatingSystems = ["windows", "linux", "macos"];
    private static readonly string[] Architectures = ["x64", "arm64"];

    private DeviceMetadata(
        string os, string osVersion, string architecture, string managerVersion,
        ClaimedKeyProtection claimedKeyProtection, string? hostLabel)
    {
        Os = os;
        OsVersion = osVersion;
        Architecture = architecture;
        ManagerVersion = managerVersion;
        ClaimedKeyProtection = claimedKeyProtection;
        HostLabel = hostLabel;
    }

    public string Os { get; }

    public string OsVersion { get; }

    public string Architecture { get; }

    public string ManagerVersion { get; }

    // Claimed, not attested; read only by the FR-041 software-key refusal, never by a transition.
    public ClaimedKeyProtection ClaimedKeyProtection { get; }

    public string? HostLabel { get; }

    public static bool TryCreate(
        string? os, string? osVersion, string? architecture, string? managerVersion,
        string? claimedKeyProtection, string? hostLabel,
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

        if (hostLabel is not null && (hostLabel.Length == 0 || hostLabel.Length > 64))
        {
            errors.Add(new("hostLabel", "Optional, 1 to 64 characters."));
        }

        violations = errors;
        metadata = errors.Count == 0
            ? new DeviceMetadata(os!, osVersion!, architecture!, managerVersion!, protection, hostLabel)
            : null;
        return errors.Count == 0;
    }
}
