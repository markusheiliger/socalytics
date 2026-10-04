namespace SocAlytics.Platform.Persistence;

/// <summary>Signals that a versioned update matched no row because the expected version was stale.</summary>
public sealed class ConcurrencyConflictException()
    : Exception("The write was rejected because the expected version no longer matches the persisted version.");
