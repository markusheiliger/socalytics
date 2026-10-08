namespace SocAlytics.Platform.Application.Abstractions.Persistence;

public enum VersionedWriteOutcome
{
    Applied,
    NotFound,
    ConcurrencyConflict,
}

public readonly record struct VersionedWriteResult(VersionedWriteOutcome Outcome, long? Version);
