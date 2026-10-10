namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>
/// Admits work only in <see cref="OperatingStateKind.Running"/> and only after Active was confirmed
/// in this process: the gate is closed until a state machine is attached. Nothing is persisted.
/// </summary>
public sealed class AdmissionGate
{
    private readonly object gate = new();
    private OperatingStateMachine? machine;

    public void Attach(OperatingStateMachine confirmedMachine)
    {
        lock (gate) { machine = confirmedMachine; }
    }

    public void Detach()
    {
        lock (gate) { machine = null; }
    }

    public bool TryAdmit()
    {
        OperatingStateMachine? current;
        lock (gate) { current = machine; }
        return current is { IsRevoked: false } && current.State.Kind == OperatingStateKind.Running;
    }
}
