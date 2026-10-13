namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>Local validation of the optional host label (trimmed, 1–64 characters, no control characters).</summary>
public static class HostLabel
{
    public const int MaxLength = 64;

    /// <summary>Returns the trimmed label, or null with a message when the label is invalid.</summary>
    public static string? TryNormalize(string label, out string? problem)
    {
        var trimmed = label.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
        {
            problem = $"The host label must be 1 to {MaxLength} characters long.";
            return null;
        }

        if (trimmed.Any(char.IsControl))
        {
            problem = "The host label must not contain control characters.";
            return null;
        }

        problem = null;
        return trimmed;
    }
}
