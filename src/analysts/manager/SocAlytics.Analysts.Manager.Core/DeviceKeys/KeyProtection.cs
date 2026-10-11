namespace SocAlytics.Analysts.Manager.DeviceKeys;

public enum KeyProtection
{
    Tpm,
    SecureEnclave,
    Pkcs11Token,
    Software,
}

public static class KeyProtectionExtensions
{
    public static string ToWireValue(this KeyProtection protection) => protection switch
    {
        KeyProtection.Tpm => "tpm",
        KeyProtection.SecureEnclave => "secure-enclave",
        KeyProtection.Pkcs11Token => "pkcs11-token",
        KeyProtection.Software => "software",
        _ => throw new ArgumentOutOfRangeException(nameof(protection)),
    };

    public static KeyProtection FromWireValue(string value) => value switch
    {
        "tpm" => KeyProtection.Tpm,
        "secure-enclave" => KeyProtection.SecureEnclave,
        "pkcs11-token" => KeyProtection.Pkcs11Token,
        "software" => KeyProtection.Software,
        _ => throw new FormatException("Unknown key protection."),
    };
}
