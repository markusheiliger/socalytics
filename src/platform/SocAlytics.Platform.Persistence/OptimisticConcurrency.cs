namespace SocAlytics.Platform.Persistence;

public static class OptimisticConcurrency
{
    public static void EnsureSingleRowAffected(int affectedRows)
    {
        if (affectedRows == 0)
        {
            throw new OptimisticConcurrencyConflictException();
        }

        if (affectedRows != 1)
        {
            throw new InvalidOperationException("An optimistic concurrency update must affect exactly one row.");
        }
    }
}

public sealed class OptimisticConcurrencyConflictException()
    : InvalidOperationException("The persisted version no longer matches the expected version.");
