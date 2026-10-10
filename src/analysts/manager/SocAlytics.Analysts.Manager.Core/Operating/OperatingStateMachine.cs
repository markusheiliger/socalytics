namespace SocAlytics.Analysts.Manager.Operating;

/// <summary>
/// Pure operating-state machine. It is created only once an Active registration is confirmed.
/// Every transition method returns false when the event is refused in the current state.
/// </summary>
public sealed class OperatingStateMachine
{
    private readonly object gate = new();
    private OperatingState state = new(OperatingStateKind.RuntimeUnavailable);
    private OperatingIntent intent;
    private bool preflightPassed;
    private bool revoked;

    public OperatingStateMachine(OperatingIntent intent)
    {
        this.intent = intent;
    }

    public event EventHandler? Changed;

    public OperatingState State
    {
        get { lock (gate) { return state; } }
    }

    public OperatingIntent Intent
    {
        get { lock (gate) { return intent; } }
    }

    public bool IsRevoked
    {
        get { lock (gate) { return revoked; } }
    }

    public bool PreflightPassed()
    {
        return Apply(() =>
        {
            preflightPassed = true;
            switch (state.Kind)
            {
                case OperatingStateKind.RuntimeUnavailable:
                    state = new(intent.IsPaused ? OperatingStateKind.Paused : OperatingStateKind.Running);
                    return true;
                case OperatingStateKind.Running:
                case OperatingStateKind.Paused:
                case OperatingStateKind.Draining:
                    return true;
                default:
                    return false;
            }
        });
    }

    public bool PreflightFailed() => Apply(() =>
    {
        preflightPassed = false;
        switch (state.Kind)
        {
            case OperatingStateKind.Running:
            case OperatingStateKind.Paused:
                state = new(OperatingStateKind.RuntimeUnavailable);
                return true;
            case OperatingStateKind.RuntimeUnavailable:
                return true;
            case OperatingStateKind.Draining:
                state = state with { Prior = OperatingStateKind.RuntimeUnavailable };
                return true;
            default:
                return false;
        }
    });

    public bool RuntimeLost() => PreflightFailed();

    public bool Pause() => SetPause(OperatingIntent.Paused);

    public bool PauseUntil(DateTimeOffset endUtc) => SetPause(OperatingIntent.PausedUntil(endUtc));

    public bool Resume() => Apply(() =>
    {
        if (state.Kind is OperatingStateKind.Draining or OperatingStateKind.Ended)
        {
            return false;
        }

        intent = OperatingIntent.Running with { PendingUnregister = intent.PendingUnregister };
        if (state.Kind == OperatingStateKind.Paused && preflightPassed)
        {
            state = new(OperatingStateKind.Running);
        }

        return true;
    });

    public bool TimedPauseEnded() => Apply(() =>
    {
        if (state.Kind is OperatingStateKind.Draining or OperatingStateKind.Ended
            || intent.Kind != OperatingIntentKind.PausedUntil)
        {
            return false;
        }

        intent = OperatingIntent.Running with { PendingUnregister = intent.PendingUnregister };
        if (state.Kind == OperatingStateKind.Paused && preflightPassed)
        {
            state = new(OperatingStateKind.Running);
        }

        return true;
    });

    public bool BeginDrain(DrainPurpose purpose, DateTimeOffset deadline) => Apply(() =>
    {
        if (state.Kind is OperatingStateKind.Draining or OperatingStateKind.Ended)
        {
            return false;
        }

        state = new(OperatingStateKind.Draining, purpose, state.Kind, deadline);
        return true;
    });

    public bool DrainCancelled() => Apply(() =>
    {
        if (state.Kind != OperatingStateKind.Draining)
        {
            return false;
        }

        state = new(state.Prior ?? OperatingStateKind.RuntimeUnavailable);
        return true;
    });

    public bool DrainCompleted() => Apply(() =>
    {
        if (state.Kind != OperatingStateKind.Draining)
        {
            return false;
        }

        state = new(OperatingStateKind.Ended);
        return true;
    });

    public bool Revoked() => Apply(() =>
    {
        if (revoked)
        {
            return false;
        }

        revoked = true;
        state = new(OperatingStateKind.Ended);
        return true;
    });

    private bool SetPause(OperatingIntent newIntent) => Apply(() =>
    {
        switch (state.Kind)
        {
            case OperatingStateKind.Running:
                state = new(OperatingStateKind.Paused);
                break;
            case OperatingStateKind.Paused:
            case OperatingStateKind.RuntimeUnavailable:
                break;
            default:
                return false;
        }

        intent = newIntent with { PendingUnregister = intent.PendingUnregister };
        return true;
    });

    private bool Apply(Func<bool> transition)
    {
        bool changed;
        lock (gate)
        {
            var before = (state, intent);
            if (!transition())
            {
                return false;
            }

            changed = !Equals(before.state, state) || !Equals(before.intent, intent);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }
}
