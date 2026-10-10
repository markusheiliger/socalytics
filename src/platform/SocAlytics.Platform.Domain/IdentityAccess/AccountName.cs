using System.Diagnostics.CodeAnalysis;

namespace SocAlytics.Platform.Domain.IdentityAccess;

public sealed record AccountName
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    private AccountName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public string Normalized => Value.ToUpperInvariant();

    // Error codes: required, too-long, too-short, invalid.
    public static bool TryCreate(string? input, [NotNullWhen(true)] out AccountName? accountName, [NotNullWhen(false)] out string? errorCode)
    {
        accountName = null;

        if (string.IsNullOrEmpty(input))
        {
            errorCode = "required";
            return false;
        }

        if (input.Length > MaxLength)
        {
            errorCode = "too-long";
            return false;
        }

        if (input.Length < MinLength)
        {
            errorCode = "too-short";
            return false;
        }

        foreach (var c in input)
        {
            if (!IsAllowed(c))
            {
                errorCode = "invalid";
                return false;
            }
        }

        accountName = new AccountName(input);
        errorCode = null;
        return true;
    }

    public override string ToString() => Value;

    private static bool IsAllowed(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '@';
}
