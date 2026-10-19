using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.Registration;

/// <summary>
/// The stamp's minimum Manager version as last received, and whether this build is below it. In memory only:
/// a restart starts not below until the first status response, and an unparsable minimum counts as below.
/// </summary>
public sealed class MinimumVersionState(ManagerBuildVersion? build = null)
{
    private readonly object _gate = new();
    private string? _minimum;
    private bool _refusedByPlatform;

    public string BuildVersion { get; } = (build ?? ManagerBuildVersion.Current).Value;

    public string? Minimum
    {
        get { lock (_gate) { return _minimum; } }
    }

    public bool RefusedByPlatform
    {
        get { lock (_gate) { return _refusedByPlatform; } }
    }

    public bool IsBelow
    {
        get
        {
            lock (_gate)
            {
                return _refusedByPlatform || ManagerVersionComparer.IsBelow(BuildVersion, _minimum);
            }
        }
    }

    /// <summary>Applies the minimum of a successful status response and clears a platform refusal.</summary>
    public void Update(string? minimum)
    {
        lock (_gate)
        {
            _minimum = minimum;
            _refusedByPlatform = false;
        }
    }

    /// <summary>Records a version refusal of the platform; the next successful status response clears it.</summary>
    public void MarkRefused(string? minimum)
    {
        lock (_gate)
        {
            _refusedByPlatform = true;
            if (minimum is not null)
            {
                _minimum = minimum;
            }
        }
    }
}
