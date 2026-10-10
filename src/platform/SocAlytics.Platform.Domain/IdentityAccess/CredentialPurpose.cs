namespace SocAlytics.Platform.Domain.IdentityAccess;

public enum CredentialPurpose
{
    SetPassword,
    PasswordReset,
}

public static class CredentialPurposeExtensions
{
    public static string ToWireValue(this CredentialPurpose purpose) => purpose switch
    {
        CredentialPurpose.SetPassword => "set-password",
        CredentialPurpose.PasswordReset => "password-reset",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };

    public static CredentialPurpose FromWireValue(string value) => value switch
    {
        "set-password" => CredentialPurpose.SetPassword,
        "password-reset" => CredentialPurpose.PasswordReset,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
