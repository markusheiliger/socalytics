namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record RedeemCredentialCommand(string? AccountName, string? Credential, string? NewPassword);
