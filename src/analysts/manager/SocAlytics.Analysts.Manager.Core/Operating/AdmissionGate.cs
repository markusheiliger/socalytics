namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>
/// Admits work only in <see cref="OperatingStateKind.Running"/> and only after Active was confirmed
/// in this process: the gate is closed until a state machine is attached. Nothing is persisted.
/// </summary>
public sealed class AdmissionGate(Registration.MinimumVersionState? minimumVersion = null)
{
    private readonly object gate = new();
    private OperatingStateMachine? machine;
    private bool unregisterHold;

    public OperatingStateMachine? Machine
    {
        get { lock (gate) { return machine; } }
    }

    public void Attach(OperatingStateMachine confirmedMachine)
    {
        lock (gate) { machine = confirmedMachine; }
    }

    public void Detach()
    {
        lock (gate) { machine = null; }
    }

    public void HoldForUnregister()
    {
        lock (gate) { unregisterHold = true; }
    }

    public void ReleaseUnregisterHold()
    {
        lock (gate) { unregisterHold = false; }
    }

    public bool TryAdmit()
    {
        OperatingStateMachine? current;
        bool held;
        lock (gate) { current = machine; held = unregisterHold; }
        return !held && minimumVersion is not { IsBelow: true } && current is { IsRevoked: false } && current.State.Kind == OperatingStateKind.Running;
    }
}
