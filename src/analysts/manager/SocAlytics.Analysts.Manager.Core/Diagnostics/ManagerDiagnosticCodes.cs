namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>Bounded status diagnostics; never returned as error codes.</summary>
public static class ManagerDiagnosticCodes
{
    public const string StateMissing = "state-missing";
    public const string StateUnreadable = "state-unreadable";
    public const string StatePermissions = "state-permissions";
    public const string StateSignature = "state-signature";
    public const string KeyMissing = "key-missing";
    public const string KeyInaccessible = "key-inaccessible";
    public const string KeyMismatch = "key-mismatch";
    public const string StampMismatch = "stamp-mismatch";
    public const string NoSupportedKeyStore = "no-supported-key-store";
    public const string SoftwareKeyNotAllowed = "software-key-not-allowed";
    public const string PairingRefused = "pairing-refused";
    public const string InsecureEndpoint = "insecure-endpoint";
    public const string PlatformUnreachable = "platform-unreachable";
    public const string UnregisterCancelled = "unregister-cancelled";

    public static IReadOnlyList<string> All { get; } =
    [
        StateMissing, StateUnreadable, StatePermissions, StateSignature, KeyMissing, KeyInaccessible,
        KeyMismatch, StampMismatch, NoSupportedKeyStore, SoftwareKeyNotAllowed, PairingRefused,
        InsecureEndpoint, PlatformUnreachable, UnregisterCancelled,
    ];

    public static bool IsKnown(string code) => All.Contains(code);
}
