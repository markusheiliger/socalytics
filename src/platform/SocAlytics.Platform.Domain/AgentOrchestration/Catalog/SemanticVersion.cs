using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

// SemVer 2.0.0 without build metadata.
public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
	private readonly string[] _preRelease;

	private SemanticVersion(int major, int minor, int patch, string[] preRelease)
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		_preRelease = preRelease;
	}

	public int Major { get; }

	public int Minor { get; }

	public int Patch { get; }

	public bool IsPreRelease => _preRelease.Length > 0;

	public static bool TryParse(string? value, out SemanticVersion version)
	{
		version = null!;
		if (value is null || value.Length > 128)
		{
			return false;
		}

		var match = Pattern().Match(value);
		if (!match.Success
			|| !int.TryParse(match.Groups["major"].Value, out var major)
			|| !int.TryParse(match.Groups["minor"].Value, out var minor)
			|| !int.TryParse(match.Groups["patch"].Value, out var patch))
		{
			return false;
		}

		var preRelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
		version = new SemanticVersion(major, minor, patch, preRelease);
		return true;
	}

	public static int? MajorOf(string? value) => TryParse(value, out var version) ? version.Major : null;

	public int CompareTo(SemanticVersion? other)
	{
		if (other is null)
		{
			return 1;
		}

		var core = Major.CompareTo(other.Major);
		if (core != 0)
		{
			return core;
		}

		core = Minor.CompareTo(other.Minor);
		if (core != 0)
		{
			return core;
		}

		core = Patch.CompareTo(other.Patch);
		if (core != 0)
		{
			return core;
		}

		// A version without a pre-release has higher precedence than one with it.
		if (_preRelease.Length == 0 || other._preRelease.Length == 0)
		{
			return other._preRelease.Length.CompareTo(_preRelease.Length);
		}

		var count = Math.Min(_preRelease.Length, other._preRelease.Length);
		for (var i = 0; i < count; i++)
		{
			var result = ComparePreReleaseIdentifier(_preRelease[i], other._preRelease[i]);
			if (result != 0)
			{
				return result;
			}
		}

		return _preRelease.Length.CompareTo(other._preRelease.Length);
	}

	public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

	public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _preRelease));

	public override string ToString() =>
		$"{Major}.{Minor}.{Patch}{(IsPreRelease ? "-" + string.Join('.', _preRelease) : string.Empty)}";

	public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

	public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

	public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

	public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

	private static int ComparePreReleaseIdentifier(string left, string right)
	{
		var leftNumeric = ulong.TryParse(left, out var leftNumber);
		var rightNumeric = ulong.TryParse(right, out var rightNumber);
		if (leftNumeric && rightNumeric)
		{
			return leftNumber.CompareTo(rightNumber);
		}

		if (leftNumeric)
		{
			return -1;
		}

		if (rightNumeric)
		{
			return 1;
		}

		return Math.Sign(string.CompareOrdinal(left, right));
	}

	[GeneratedRegex(@"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$", RegexOptions.CultureInvariant)]
	private static partial Regex Pattern();
}
