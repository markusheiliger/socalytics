using SocAlytics.Analysts.Manager.Operating;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>
/// Fails closed when the protected store breaks while running: no fallback, no deletion, no rebind.
/// Recovery is a restart (which re-runs the restore) or discarding the local registration.
/// </summary>
public sealed class ProtectedStoreFailureHandler(RegistrationCoordinator coordinator, AdmissionGate admission)
{
    /// <summary>Returns true when the failure was handled (the Manager was active or unregistering).</summary>
    public bool Handle(string code)
    {
        if (coordinator.State is not (ManagerRegistrationState.Active or ManagerRegistrationState.Unregistering))
        {
            return false;
        }

        admission.Detach();
        coordinator.FailRestore(code);
        return true;
    }
}
