using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SocAlytics.Platform.Domain.Registry;

public sealed class ManagerVersion : IComparable<ManagerVersion>, IEquatable<ManagerVersion>
{
    public const int MaxLength = 64;

    private readonly BigInteger major;
    private readonly BigInteger minor;
    private readonly BigInteger patch;

    private ManagerVersion(string value, BigInteger major, BigInteger minor, BigInteger patch)
    {
        Value = value;
        this.major = major;
        this.minor = minor;
        this.patch = patch;
    }

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out ManagerVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3
            || !TryParsePart(parts[0], out var major)
            || !TryParsePart(parts[1], out var minor)
            || !TryParsePart(parts[2], out var patch))
        {
            return false;
        }

        version = new ManagerVersion(value, major, minor, patch);
        return true;
    }

    public static bool IsBelow(string? reported, ManagerVersion? minimum)
    {
        if (minimum is null)
        {
            return false;
        }

        return !TryParse(reported, out var version) || version.CompareTo(minimum) < 0;
    }

    public int CompareTo(ManagerVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = major.CompareTo(other.major);
        if (result != 0)
        {
            return result;
        }

        result = minor.CompareTo(other.minor);
        return result != 0 ? result : patch.CompareTo(other.patch);
    }

    public bool Equals(ManagerVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ManagerVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(major, minor, patch);

    public override string ToString() => Value;

    // Digits only without leading zeros, as the status contract's pattern requires.
    private static bool TryParsePart(string part, out BigInteger number)
    {
        number = BigInteger.Zero;
        if (part.Length == 0 || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        foreach (var c in part)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        number = BigInteger.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}
