namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record ApplyBreakGlassRecoveryCommand(string? AccountName, string? RecoveryId, string? TemporaryCredential)
{
    public override string ToString() => nameof(ApplyBreakGlassRecoveryCommand);
}
