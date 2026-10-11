namespace SocAlytics.Platform.Domain.Registry;

public enum RegistrationState
{
    Pairing,
    PendingApproval,
    Approved,
    Active,
    Unregistering,
    Unregistered,
    Rejected,
    Expired,
    Revoked,
}

public static class RegistrationStateExtensions
{
    public static bool IsTerminal(this RegistrationState state) =>
        state is RegistrationState.Unregistered or RegistrationState.Rejected
            or RegistrationState.Expired or RegistrationState.Revoked;

    public static string ToApiName(this RegistrationState state) => state switch
    {
        RegistrationState.Pairing => "pairing",
        RegistrationState.PendingApproval => "pending_approval",
        RegistrationState.Approved => "approved",
        RegistrationState.Active => "active",
        RegistrationState.Unregistering => "unregistering",
        RegistrationState.Unregistered => "unregistered",
        RegistrationState.Rejected => "rejected",
        RegistrationState.Expired => "expired",
        RegistrationState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParseApiName(string? name, out RegistrationState state)
    {
        foreach (var candidate in Enum.GetValues<RegistrationState>())
        {
            if (string.Equals(candidate.ToApiName(), name, StringComparison.Ordinal))
            {
                state = candidate;
                return true;
            }
        }

        state = default;
        return false;
    }
}
