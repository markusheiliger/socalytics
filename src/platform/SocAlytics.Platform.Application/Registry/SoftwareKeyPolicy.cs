using System.ComponentModel;
using System.Globalization;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry;

[TypeConverter(typeof(SoftwareKeyPolicyConverter))]
public enum SoftwareKeyPolicy
{
    Refused,
    HeadlessServerOnly,
    All,
}

public static class SoftwareKeyPolicyExtensions
{
    public static string ToConfigurationName(this SoftwareKeyPolicy policy) => policy switch
    {
        SoftwareKeyPolicy.Refused => "refused",
        SoftwareKeyPolicy.HeadlessServerOnly => "headless-server-only",
        SoftwareKeyPolicy.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    // An absent claim counts as tray.
    public static bool Accepts(this SoftwareKeyPolicy policy, ClaimedApplicationProfile? claimed) => policy switch
    {
        SoftwareKeyPolicy.All => true,
        SoftwareKeyPolicy.HeadlessServerOnly => claimed == ClaimedApplicationProfile.HeadlessServer,
        _ => false,
    };

    public static IReadOnlyList<string> AllowedProfiles(this SoftwareKeyPolicy policy) => policy switch
    {
        SoftwareKeyPolicy.All => ["tray", "headless-server"],
        SoftwareKeyPolicy.HeadlessServerOnly => ["headless-server"],
        _ => [],
    };
}

// Lets the configuration binder read refused, headless-server-only, and all.
public sealed class SoftwareKeyPolicyConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
        {
            foreach (var candidate in Enum.GetValues<SoftwareKeyPolicy>())
            {
                if (string.Equals(candidate.ToConfigurationName(), text.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            throw new FormatException("Registry:SoftwareKeys must be refused, headless-server-only, or all.");
        }

        return base.ConvertFrom(context, culture, value);
    }
}
