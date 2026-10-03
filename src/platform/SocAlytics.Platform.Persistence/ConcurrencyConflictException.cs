namespace SocAlytics.Platform.Persistence;

/// <summary>An update guarded by an expected version affected no row.</summary>
public sealed class ConcurrencyConflictException()
    : Exception("The write was rejected because the expected version no longer matches the persisted version.");
