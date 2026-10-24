namespace SocAlytics.Platform.Domain.IdentityAccess;

/// <summary>The stored state of an invitation; <c>expired</c> is derived, never stored.</summary>
public enum InvitationState
{
    Pending,
    Accepted,
    Revoked,
}

/// <summary>The state an invitation is shown in: the stored state, or <c>expired</c> for an open link past its expiry.</summary>
public enum InvitationDisplayState
{
    Pending,
    Expired,
    Accepted,
    Revoked,
}

public static class InvitationStateRules
{
    public static string ToWireValue(this InvitationState state) => state switch
    {
        InvitationState.Pending => "pending",
        InvitationState.Accepted => "accepted",
        InvitationState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParse(string? wireValue, out InvitationState state)
    {
        switch (wireValue)
        {
            case "pending":
                state = InvitationState.Pending;
                return true;
            case "accepted":
                state = InvitationState.Accepted;
                return true;
            case "revoked":
                state = InvitationState.Revoked;
                return true;
            default:
                state = default;
                return false;
        }
    }

    public static string ToWireValue(this InvitationDisplayState state) => state switch
    {
        InvitationDisplayState.Pending => "pending",
        InvitationDisplayState.Expired => "expired",
        InvitationDisplayState.Accepted => "accepted",
        InvitationDisplayState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParse(string? wireValue, out InvitationDisplayState state)
    {
        switch (wireValue)
        {
            case "pending":
                state = InvitationDisplayState.Pending;
                return true;
            case "expired":
                state = InvitationDisplayState.Expired;
                return true;
            case "accepted":
                state = InvitationDisplayState.Accepted;
                return true;
            case "revoked":
                state = InvitationDisplayState.Revoked;
                return true;
            default:
                state = default;
                return false;
        }
    }
}

/// <summary>
/// A pending offer of membership to one address. The open link is the invitation's newest link that is neither
/// <c>used</c> nor <c>superseded</c>: <paramref name="OpenLinkIssuedAt"/> and <paramref name="OpenLinkExpiresAt"/>
/// are <c>null</c> while that link is not yet issued (or when there is no open link).
/// </summary>
public sealed record MemberInvitation(
    Guid Id,
    string Address,
    AccountName AccountName,
    InvitationState State,
    Guid InvitedBy,
    DateTimeOffset CreatedAt,
    int ResendCount,
    Guid? RevokedBy,
    DateTimeOffset? RevokedAt,
    Guid? AcceptedMemberId,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? OpenLinkIssuedAt,
    DateTimeOffset? OpenLinkExpiresAt,
    long Version)
{
    public InvitationDisplayState DisplayState(DateTimeOffset now) => State switch
    {
        InvitationState.Accepted => InvitationDisplayState.Accepted,
        InvitationState.Revoked => InvitationDisplayState.Revoked,
        _ => IsOpenLinkExpired(now) ? InvitationDisplayState.Expired : InvitationDisplayState.Pending,
    };

    // pending or expired --resend--> pending
    public bool CanResend() => State == InvitationState.Pending;

    // pending or expired --revoke--> revoked
    public bool CanRevoke() => State == InvitationState.Pending;

    // pending, with an issued and unexpired open link --accept--> accepted
    public bool CanAccept(DateTimeOffset now) =>
        State == InvitationState.Pending && OpenLinkIssuedAt is not null && !IsOpenLinkExpired(now);

    private bool IsOpenLinkExpired(DateTimeOffset now) =>
        OpenLinkIssuedAt is not null && OpenLinkExpiresAt is { } expiresAt && expiresAt <= now;
}
