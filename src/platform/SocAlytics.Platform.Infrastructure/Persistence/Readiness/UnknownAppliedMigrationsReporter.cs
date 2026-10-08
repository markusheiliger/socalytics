namespace SocAlytics.Platform.Infrastructure.Persistence.Readiness;

internal sealed class UnknownAppliedMigrationsReporter
{
    private readonly object _gate = new();
    private string _lastReported = string.Empty;

    /// <summary>Returns true when the set differs from the last reported one; an empty set resets the state.</summary>
    public bool ShouldReport(IReadOnlyList<string> identities)
    {
        var key = string.Join('\n', identities);
        lock (_gate)
        {
            if (string.Equals(_lastReported, key, StringComparison.Ordinal))
            {
                return false;
            }

            _lastReported = key;
            return key.Length > 0;
        }
    }
}
