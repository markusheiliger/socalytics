namespace SocAlytics.Platform.Persistence;

/// <summary>Signals that a versioned write matched no row because the expected version was stale.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
        : base("The write was rejected because the expected version no longer matches the persisted version.")
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
