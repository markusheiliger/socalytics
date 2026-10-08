using System.Diagnostics.CodeAnalysis;

namespace SocAlytics.Platform.Domain.Club;

public readonly record struct DisplayName
{
    public const int MaxLength = 100;

    private DisplayName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? input, out DisplayName displayName, [NotNullWhen(false)] out string? errorCode)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            displayName = default;
            errorCode = "required";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            displayName = default;
            errorCode = "too-long";
            return false;
        }

        displayName = new DisplayName(trimmed);
        errorCode = null;
        return true;
    }

    public override string ToString() => Value;
}
