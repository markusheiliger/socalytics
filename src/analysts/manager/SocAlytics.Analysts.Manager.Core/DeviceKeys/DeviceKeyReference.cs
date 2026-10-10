namespace SocAlytics.Analysts.Manager.DeviceKeys;

public sealed record DeviceKeyReference(string Provider, string Reference)
{
    public const string WindowsCng = "windows-cng";
    public const string MacosBridge = "macos-bridge";
    public const string Pkcs11 = "pkcs11";
}
