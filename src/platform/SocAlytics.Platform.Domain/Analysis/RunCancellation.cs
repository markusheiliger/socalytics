using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Analysis;

public static class RunCancellation
{
	public const int MaxReasonLength = 500;

	// Optional; trimmed; empty becomes null; at most 500 characters; control characters
	// are refused except "\n" and "\r\n".
	public static bool TryNormalizeReason(string? reason, out string? normalized, out FieldViolation? violation)
	{
		normalized = null;
		violation = null;

		var trimmed = reason?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return true;
		}

		if (trimmed.Length > MaxReasonLength)
		{
			violation = new("reason", $"Optional, at most {MaxReasonLength} characters.");
			return false;
		}

		for (var index = 0; index < trimmed.Length; index++)
		{
			var character = trimmed[index];
			if (!char.IsControl(character) || character == '\n')
			{
				continue;
			}

			if (character == '\r' && index + 1 < trimmed.Length && trimmed[index + 1] == '\n')
			{
				continue;
			}

			violation = new("reason", "Must not contain control characters other than line breaks.");
			return false;
		}

		normalized = trimmed;
		return true;
	}

	// A repeat of a cancellation is accepted with the current version, or with the version
	// from before the cancellation (current - 1).
	public static bool IsIdempotentRepeatVersion(long currentVersion, long ifMatchVersion) =>
		ifMatchVersion == currentVersion || ifMatchVersion == currentVersion - 1;
}
