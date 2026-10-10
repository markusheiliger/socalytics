namespace SocAlytics.Analysts.Manager.DeviceKeys;

/// <summary>Production allow-list: only the configured tpm2-pkcs11 module; SoftHSM2 is never on it.</summary>
internal sealed class Pkcs11ModuleAllowList(string tpm2ModulePath)
{
    public bool IsAllowed(string modulePath) =>
        !string.IsNullOrWhiteSpace(modulePath)
        && string.Equals(modulePath, tpm2ModulePath, StringComparison.Ordinal)
        && Path.GetFileName(modulePath).Contains("tpm2_pkcs11", StringComparison.Ordinal);
}
