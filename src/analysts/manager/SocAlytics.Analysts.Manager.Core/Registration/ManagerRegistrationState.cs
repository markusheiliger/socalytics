namespace SocAlytics.Analysts.Manager.Registration;

public enum ManagerRegistrationState
{
    Unregistered,
    Pairing,
    Restoring,
    Active,
    Unregistering,
    Revoked,
    RestoreFailed,
}

public enum PairingPhase
{
    AwaitingSubmission,
    PendingApproval,
    Activating,
}

public static class RegistrationWireValues
{
    public static string ToWireValue(this ManagerRegistrationState state) => state switch
    {
        ManagerRegistrationState.Unregistered => "unregistered",
        ManagerRegistrationState.Pairing => "pairing",
        ManagerRegistrationState.Restoring => "restoring",
        ManagerRegistrationState.Active => "active",
        ManagerRegistrationState.Unregistering => "unregistering",
        ManagerRegistrationState.Revoked => "revoked",
        ManagerRegistrationState.RestoreFailed => "restore-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string ToWireValue(this PairingPhase phase) => phase switch
    {
        PairingPhase.AwaitingSubmission => "awaiting-submission",
        PairingPhase.PendingApproval => "pending-approval",
        PairingPhase.Activating => "activating",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}
