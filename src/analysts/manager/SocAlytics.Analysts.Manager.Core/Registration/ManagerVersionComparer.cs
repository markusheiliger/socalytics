using System.Numerics;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>Compares MAJOR.MINOR.PATCH versions exactly like the platform's ManagerVersion.</summary>
public static class ManagerVersionComparer
{
    private const int MaxLength = 64;

    /// <summary>True when a minimum is set and the running version is missing, malformed, or orders before it; an unparsable minimum counts as below.</summary>
    public static bool IsBelow(string? running, string? minimum)
    {
        if (minimum is null)
        {
            return false;
        }

        if (!TryParse(minimum, out var min) || !TryParse(running, out var current))
        {
            return true;
        }

        return Compare(current, min) < 0;
    }

    public static bool TryParse(string? value, out (BigInteger Major, BigInteger Minor, BigInteger Patch) version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3 || !parts.All(IsNumber))
        {
            return false;
        }

        version = (BigInteger.Parse(parts[0]), BigInteger.Parse(parts[1]), BigInteger.Parse(parts[2]));
        return true;
    }

    private static bool IsNumber(string part) =>
        part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    private static int Compare((BigInteger Major, BigInteger Minor, BigInteger Patch) a, (BigInteger Major, BigInteger Minor, BigInteger Patch) b)
    {
        var major = a.Major.CompareTo(b.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = a.Minor.CompareTo(b.Minor);
        return minor != 0 ? minor : a.Patch.CompareTo(b.Patch);
    }
}
