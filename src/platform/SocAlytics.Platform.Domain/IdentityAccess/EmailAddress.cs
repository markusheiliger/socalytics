using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Platform.Domain.IdentityAccess;

public sealed record EmailAddress
{
    public const int MaxLength = 254;
    public const int MaxLocalPartLength = 64;
    public const int MaxLabelLength = 63;

    private const string LocalPartSpecials = "!#$%&'*+/=?^_`{|}~-";

    private EmailAddress(string value, string localPart, string domain)
    {
        Value = value;
        Normalized = value.ToLowerInvariant();
        LocalPart = localPart.ToLowerInvariant();
        Domain = domain.ToLowerInvariant();
    }

    // The trimmed address as entered.
    public string Value { get; }

    public string Normalized { get; }

    public string LocalPart { get; }

    public string Domain { get; }

    // Lower-case hexadecimal SHA-256 of the normalized address.
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalized)));

    // Masked as j***@e***.com: first character of the local part and of the first domain label, and the last domain label.
    public string Masked
    {
        get
        {
            var lastDot = Domain.LastIndexOf('.');
            return $"{LocalPart[0]}***@{Domain[0]}***{Domain[lastDot..]}";
        }
    }

    // Error codes: required, too-long, invalid.
    public static bool TryCreate(string? input, [NotNullWhen(true)] out EmailAddress? emailAddress, [NotNullWhen(false)] out string? errorCode)
    {
        emailAddress = null;

        var value = input?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            errorCode = "required";
            return false;
        }

        if (value.Length > MaxLength)
        {
            errorCode = "too-long";
            return false;
        }

        var at = value.IndexOf('@');
        if (at < 1 || at != value.LastIndexOf('@'))
        {
            errorCode = "invalid";
            return false;
        }

        var localPart = value[..at];
        var domain = value[(at + 1)..];
        if (!IsValidLocalPart(localPart) || !IsValidDomain(domain))
        {
            errorCode = "invalid";
            return false;
        }

        emailAddress = new EmailAddress(value, localPart, domain);
        errorCode = null;
        return true;
    }

    public override string ToString() => Masked;

    private static bool IsValidLocalPart(string localPart)
    {
        if (localPart.Length > MaxLocalPartLength || localPart[0] == '.' || localPart[^1] == '.' || localPart.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in localPart)
        {
            if (!(IsAsciiLetterOrDigit(c) || c == '.' || LocalPartSpecials.Contains(c, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    // At least two labels, so that the masked form always has a top-level label.
    private static bool IsValidDomain(string domain)
    {
        var labels = domain.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
