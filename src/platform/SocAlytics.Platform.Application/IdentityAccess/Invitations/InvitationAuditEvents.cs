namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public static class InvitationAuditEvents
{
    public const string Created = "invitation.created";
    public const string Issued = "invitation.issued";
    public const string Resent = "invitation.resent";
    public const string Revoked = "invitation.revoked";
    public const string Accepted = "invitation.accepted";
    public const string AcceptanceRefused = "invitation.acceptance-refused";

    public const string ReasonEmailAddressInUse = "email-address-in-use";
    public const string ReasonAccountNameTaken = "account-name-taken";
    public const string ReasonRateLimited = "rate-limited";
    public const string ReasonInvitationNotOpen = "invitation-not-open";
    public const string ReasonVersionMismatch = "version-mismatch";
    public const string ReasonInvitationInvalid = "invitation-invalid";
    public const string ReasonPasswordPolicy = "password-policy";
}
