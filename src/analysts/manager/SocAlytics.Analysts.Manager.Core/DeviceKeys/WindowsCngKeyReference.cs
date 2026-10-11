namespace SocAlytics.Analysts.Manager.DeviceKeys;

internal static class WindowsCngKeyReference
{
    public const string KeyNamePrefix = "SocAlytics-AnalystManager-";
    public const string PlatformProviderName = "Microsoft Platform Crypto Provider";
    public const string SoftwareProviderName = "Microsoft Software Key Storage Provider";

    private const char Separator = '|';

    public static string NewKeyName() => KeyNamePrefix + Guid.NewGuid().ToString("N");

    public static DeviceKeyReference Format(string providerName, string keyName) =>
        new(DeviceKeyReference.WindowsCng, providerName + Separator + keyName);

    public static bool TryParse(DeviceKeyReference reference, out string providerName, out string keyName)
    {
        providerName = keyName = string.Empty;
        if (reference.Provider != DeviceKeyReference.WindowsCng)
        {
            return false;
        }

        var parts = reference.Reference.Split(Separator);
        if (parts.Length != 2
            || parts[0] is not (PlatformProviderName or SoftwareProviderName)
            || !parts[1].StartsWith(KeyNamePrefix, StringComparison.Ordinal)
            || parts[1].Length == KeyNamePrefix.Length)
        {
            return false;
        }

        providerName = parts[0];
        keyName = parts[1];
        return true;
    }
}
